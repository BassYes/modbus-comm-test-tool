using Comm.Core;
using Comm.Core.Codecs;
using Comm.Core.Frames;

namespace Comm.Protocols.Modbus;

/// <summary>
/// Modbus RTU 成帧器：长度预测为主、静默超时为辅（见规划 14.3.2）。
/// Windows 下毫秒级静默检测不可靠，因此不把它作为主要分帧手段。
/// </summary>
public sealed class RtuFramer : IFramer, IExpectationAwareFramer
{
    private const int MaxFrameLength = 256;
    private const int MinFrameLength = 4;

    private readonly FramerRole _role;
    private readonly ModbusCrc16 _crc = new();
    private readonly int _silenceThresholdMs;

    private byte[] _buffer = new byte[1024];
    private int _count;
    private byte? _expectedUnitId;
    private byte? _expectedFunction;
    private int _noiseRun;

    public RtuFramer(FramerRole role, int silenceThresholdMs = 5)
    {
        _role = role;
        _silenceThresholdMs = Math.Max(1, silenceThresholdMs);
    }

    public int PendingBytes => _count;

    public void SetExpectation(byte? unitId, byte? functionCode)
    {
        _expectedUnitId = unitId;
        _expectedFunction = functionCode;
    }

    public IReadOnlyList<FrameCandidate> Feed(ReadOnlySpan<byte> data, long monotonicTicks)
    {
        EnsureCapacity(_count + data.Length);
        data.CopyTo(_buffer.AsSpan(_count));
        _count += data.Length;
        return Drain();
    }

    public IReadOnlyList<FrameCandidate> OnTimer(long monotonicTicks)
    {
        var results = new List<FrameCandidate>();
        if (_count == 0) return results;

        var span = _buffer.AsSpan(0, _count);

        // 静默判定优先按“整体即一帧”校验：未知功能码只能靠静默定界（见规划 14.3.2 第 2 条）。
        if (span.Length >= MinFrameLength && _crc.Verify(span))
        {
            results.Add(new FrameCandidate(span.ToArray(), FrameStatus.Valid));
            Reset();
            return results;
        }

        // 否则在缓冲区内滑动寻找可用的子帧，前缀按噪声记录。
        while (span.Length >= MinFrameLength)
        {
            int found = FindValidFrame(span, out int length);
            if (found < 0) break;

            if (found > 0)
            {
                results.Add(new FrameCandidate(span[..found].ToArray(), FrameStatus.Garbage, $"噪声字节 {found} 个"));
            }

            results.Add(new FrameCandidate(span.Slice(found, length).ToArray(), FrameStatus.Valid));
            span = span[(found + length)..];
        }

        if (span.Length > 0)
        {
            if (span.Length >= MinFrameLength)
            {
                results.Add(new FrameCandidate(span.ToArray(), FrameStatus.ChecksumError, "按静默判定，未通过校验"));
            }
            else
            {
                results.Add(new FrameCandidate(span.ToArray(), FrameStatus.Truncated, "半帧（不足最小长度）"));
            }
        }

        Reset();
        return results;
    }

    public void Reset()
    {
        _count = 0;
        _noiseRun = 0;
    }

    private IReadOnlyList<FrameCandidate> Drain()
    {
        var results = new List<FrameCandidate>();
        int offset = 0;

        while (_count - offset >= 2)
        {
            var span = _buffer.AsSpan(offset, _count - offset);
            byte addr = span[0];
            byte fc = span[1];

            // 主站模式且已知当前请求：跳过不属于本次响应的字节。
            if (_role == FramerRole.Master && _expectedUnitId.HasValue)
            {
                bool addrOk = addr == _expectedUnitId.Value;
                bool fcOk = !_expectedFunction.HasValue
                            || fc == _expectedFunction.Value
                            || fc == (byte)(_expectedFunction.Value | 0x80);
                if (!addrOk || !fcOk)
                {
                    _noiseRun++;
                    offset++;
                    continue;
                }
            }

            FlushNoise(results, offset);

            int expected = PredictLength(fc, span);
            if (expected < 0)
            {
                // 长度无法预测：先在缓冲区中滑动寻找校验正确的帧；找不到则等待静默收尾。
                int found = FindValidFrame(span, out int foundLength);
                if (found < 0) break;

                if (found > 0)
                {
                    results.Add(new FrameCandidate(span[..found].ToArray(), FrameStatus.Garbage, $"噪声字节 {found} 个"));
                }

                results.Add(new FrameCandidate(span.Slice(found, foundLength).ToArray(), FrameStatus.Valid));
                offset += found + foundLength;
                continue;
            }

            if (span.Length < expected) break;

            var frame = span[..expected].ToArray();
            if (_crc.Verify(frame))
            {
                results.Add(new FrameCandidate(frame, FrameStatus.Valid));
                offset += expected;
            }
            else
            {
                results.Add(new FrameCandidate(frame, FrameStatus.ChecksumError, "CRC 校验不通过"));
                offset += 1; // 滑动重同步
            }
        }

        Compact(offset);
        return results;
    }

    private void FlushNoise(List<FrameCandidate> results, int currentOffset)
    {
        if (_noiseRun <= 0) return;

        int start = Math.Max(0, currentOffset - _noiseRun);
        int length = Math.Min(_noiseRun, Math.Max(0, _count - start));
        if (length > 0)
        {
            var noise = _buffer.AsSpan(start, length).ToArray();
            results.Add(new FrameCandidate(noise, FrameStatus.Garbage, $"忽略 {_noiseRun} 个非本次响应字节"));
        }
        _noiseRun = 0;
    }

    /// <summary>按响应规则预测总长度（含地址与 CRC）。返回 -1 表示无法预测。</summary>
    private int PredictLength(byte fc, ReadOnlySpan<byte> span)
    {
        if (Fn.IsException(fc)) return 5;

        switch (fc)
        {
            case Fn.ReadCoils:
            case Fn.ReadDiscreteInputs:
            case Fn.ReadHoldingRegisters:
            case Fn.ReadInputRegisters:
            case Fn.ReadWriteMultipleRegisters:
                if (span.Length < 3) return -1;
                return 5 + span[2];

            case Fn.WriteSingleCoil:
            case Fn.WriteSingleRegister:
            case Fn.WriteMultipleCoils:
            case Fn.WriteMultipleRegisters:
            case Fn.Diagnostics:
                return 8;

            case Fn.MaskWriteRegister:
                return 10;
        }

        // 监视模式下尝试请求规则（主站模式直接交给静默处理）。
        if (_role == FramerRole.Monitor) return PredictRequestLength(fc, span);

        return -1;
    }

    private static int PredictRequestLength(byte fc, ReadOnlySpan<byte> span) => fc switch
    {
        Fn.ReadCoils or Fn.ReadDiscreteInputs or Fn.ReadHoldingRegisters or Fn.ReadInputRegisters => 8,
        Fn.WriteSingleCoil or Fn.WriteSingleRegister => 8,
        Fn.WriteMultipleCoils or Fn.WriteMultipleRegisters => span.Length >= 7 ? 9 + span[6] : -1,
        Fn.MaskWriteRegister => 10,
        _ => -1
    };

    /// <summary>在缓冲区中寻找一个可用的帧：优先按响应规则，其次按请求规则。</summary>
    private int FindValidFrame(ReadOnlySpan<byte> span, out int length)
    {
        for (int offset = 0; offset + MinFrameLength <= span.Length; offset++)
        {
            var rest = span[offset..];
            int responseLength = PredictLength(rest[1], rest);
            if (responseLength >= MinFrameLength && rest.Length >= responseLength && _crc.Verify(rest[..responseLength]))
            {
                length = responseLength;
                return offset;
            }

            if (_role == FramerRole.Monitor)
            {
                int requestLength = PredictRequestLength(rest[1], rest);
                if (requestLength >= MinFrameLength && rest.Length >= requestLength && _crc.Verify(rest[..requestLength]))
                {
                    length = requestLength;
                    return offset;
                }
            }
        }

        length = 0;
        return -1;
    }

    private void EnsureCapacity(int required)
    {
        if (_buffer.Length >= required) return;
        int newSize = _buffer.Length;
        while (newSize < required) newSize *= 2;
        Array.Resize(ref _buffer, newSize);
    }

    private void Compact(int consumed)
    {
        if (consumed <= 0) return;
        if (consumed >= _count)
        {
            _count = 0;
            return;
        }
        Buffer.BlockCopy(_buffer, consumed, _buffer, 0, _count - consumed);
        _count -= consumed;

        // 超过最大帧长仍无法成帧：保留尾部，其余作为噪声丢弃。
        if (_count > MaxFrameLength * 2)
        {
            int drop = _count - MaxFrameLength;
            Buffer.BlockCopy(_buffer, drop, _buffer, 0, MaxFrameLength);
            _count = MaxFrameLength;
        }
    }
}
