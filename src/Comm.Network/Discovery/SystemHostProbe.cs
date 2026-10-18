using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Comm.Core;

namespace Comm.Network.Discovery;

/// <summary>
/// 真实主机探测（见规划 4.7.2）：ARP（IP Helper <c>SendARP</c>）+ ICMP（<c>Ping</c>）+ TCP 端口探测。
/// ARP 只在同一二层网段有效，路由之后拿不到 MAC——拿不到 MAC **绝不能**判定为离线；
/// 所有平台异常都必须转换成 <see cref="HostProbeResult.Error"/>，不向上抛。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SystemHostProbe : IHostProbe
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddress, ref uint physicalAddressLength);

    public async Task<HostProbeResult> ProbeAsync(string ip, LanScanOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        bool arpResponded = false;
        bool icmpResponded = false;
        bool portOpen = false;
        string? mac = null;
        string? error = null;
        long begin = Clock.MonotonicTicks;

        if (options.UseArp)
        {
            try
            {
                (arpResponded, mac) = TryArp(ip);
            }
            catch (Exception ex)
            {
                error = Append(error, ex.Message);
            }
        }

        if (options.UseIcmp)
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(ip, options.EffectiveTimeoutMs())
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                icmpResponded = reply.Status == IPStatus.Success;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = Append(error, ex.Message);
            }
        }

        foreach (var port in options.TcpPorts ?? Array.Empty<int>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (port is < 1 or > 65535) continue;

            try
            {
                if (await TryConnectAsync(ip, port, options.EffectiveTimeoutMs(), cancellationToken).ConfigureAwait(false))
                {
                    portOpen = true;
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = Append(error, ex.Message);
            }
        }

        return new HostProbeResult(arpResponded, icmpResponded, portOpen,
            Clock.MillisecondsBetween(begin, Clock.MonotonicTicks), mac, error);
    }

    /// <summary>TCP 端口探测；连接被拒绝是预期结果，不算错误。</summary>
    private static async Task<bool> TryConnectAsync(string ip, int port, int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(ip, out var address)) return false;

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);

        try
        {
            await socket.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static (bool Responded, string? Mac) TryArp(string ip)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return (false, null);
        if (!IPAddress.TryParse(ip, out var address)) return (false, null);

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return (false, null);

        // SendARP 需要网络字节序的 uint：在本机小端下反转字节即为网络序。
        uint destination = BitConverter.ToUInt32(bytes.Reverse().ToArray(), 0);
        var mac = new byte[6];
        uint length = 6;

        int status = SendARP(destination, 0, mac, ref length);
        if (status != 0 || length == 0) return (false, null);

        return (true, string.Join(':', mac.Take((int)length).Select(b => b.ToString("X2"))));
    }

    private static string Append(string? existing, string value) =>
        string.IsNullOrEmpty(existing) ? value : existing + "；" + value;
}
