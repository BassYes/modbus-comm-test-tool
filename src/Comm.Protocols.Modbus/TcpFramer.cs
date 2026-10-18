using Comm.Core.Codecs;
using Comm.Core.Frames;

namespace Comm.Protocols.Modbus;

/// <summary>Modbus TCP 成帧器：按 MBAP 长度字段重组（见规划 14.3.3）。</summary>
public sealed class TcpFramer : IFramer
{
    private const int MbapLength = 7;

    private byte[] _buffer = new byte[1024];
    private int _count;

    public int PendingBytes => _count;

    public IReadOnlyList<FrameCandidate> Feed(ReadOnlySpan<byte> data, long monotonicTicks)
    {
        EnsureCapacity(_count + data.Length);
        data.CopyTo(_buffer.AsSpan(_count));
        _count += data.Length;
        return Drain();
    }

    public IReadOnlyList<FrameCandidate> OnTimer(long monotonicTicks)
    {
        // TCP 有长度字段，静默不参与分帧。
        return Array.Empty<FrameCandidate>();
    }

    public void Reset() => _count = 0;

    private IReadOnlyList<FrameCandidate> Drain()
    {
        var results = new List<FrameCandidate>();
        int offset = 0;

        while (_count - offset >= 6)
        {
            var span = _buffer.AsSpan(offset, _count - offset);
            int length = (span[4] << 8) | span[5];
            int total = 6 + length;

            if (length < 2 || length > 254)
            {
                results.Add(new FrameCandidate(span.ToArray(), FrameStatus.LengthError,
                    $"MBAP 长度字段非法（{length}），连接缓冲已清空并等待重连"));
                offset = _count;
                break;
            }

            if (span.Length < total) break;

            var frame = span[..total].ToArray();
            ushort protocolId = (ushort)((frame[2] << 8) | frame[3]);
            if (protocolId != 0)
            {
                results.Add(new FrameCandidate(frame, FrameStatus.ProtocolError, $"协议标识非 0（{protocolId}）"));
            }
            else
            {
                results.Add(new FrameCandidate(frame, FrameStatus.Valid));
            }
            offset += total;
        }

        if (offset > 0)
        {
            if (offset >= _count) _count = 0;
            else
            {
                Buffer.BlockCopy(_buffer, offset, _buffer, 0, _count - offset);
                _count -= offset;
            }
        }

        return results;
    }

    private void EnsureCapacity(int required)
    {
        if (_buffer.Length >= required) return;
        int newSize = _buffer.Length;
        while (newSize < required) newSize *= 2;
        Array.Resize(ref _buffer, newSize);
    }
}
