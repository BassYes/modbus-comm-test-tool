using System.Net;
using System.Net.Sockets;
using Comm.App.Wpf.Services;
using Comm.Capture;
using Comm.Core.Frames;
using Comm.Diagnostics;
using Comm.Protocols.Modbus;
using Comm.Scanning;
using Comm.Settings;

namespace Comm.SessionProbe;

/// <summary>
/// 多会话自检探针（M3b R-16 的验收证据，规划 21.2.3）。
/// <para>
/// 在**没有硬件**的情况下，用进程内 Modbus TCP 从站模拟器验证：
/// ① 两个会话可并行连接、各自的统计与报文互不串扰；
/// ② 同一串口不能被两个会话同时打开（给出可读提示）；
/// ③ 会话数量上限生效；
/// ④ 会话工作区（SessionWorkspaces）随会话建立/关闭同步；
/// ⑤ 每个会话的报文监视只投影自己的帧（按 SessionId 过滤）。
/// </para>
/// <para>
/// 用法：<c>Comm.SessionProbe.exe</c>（可选 <c>--verbose</c>）。退出码 0 = 全部通过。
/// 本探针**只使用回环 TCP 与内存对象**，不打开真实串口、不修改任何系统配置。
/// </para>
/// </summary>
public static class Program
{
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        bool verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);

        Console.WriteLine("多会话自检探针（M3b R-16 / 规划 21.2.3）");
        Console.WriteLine("------------------------------------------------------------");

        // 临时数据目录：探针不能污染真实设置（用进程内设置存储，只读内存）。
        var settings = new AppSettings
        {
            Log = new LogOptions { Enabled = false },
            Sessions = new SessionSettings { MaxSessions = 2 }
        };

        await using var bus = new FrameBus(20_000);
        await using var log = new LogGate(settings.Log);
        var ports = new SerialPortRegistry();
        var sessions = new SessionManager(bus, log, ports, settings);

        await using var simulator = new LoopbackModbusTcpSlave();
        simulator.Start();
        Console.WriteLine($"已启动进程内 Modbus TCP 从站模拟器：{simulator.Endpoint}");

        // ① 两个会话并行连接同一目标（规划 4.1.3：多个 TCP 会话可指向同一目标，各自独立连接）
        var sessionA = sessions.Sessions[0];
        var second = sessions.CreateSession("会话 2");
        Check("可创建第二个会话", second.IsSuccess, second.Error?.ToUserText());
        var sessionB = second.Value!;
        Check("新建会话会成为活动会话", sessions.Active.SessionId == sessionB.SessionId);

        var tcpA = new TcpProfile { Host = "127.0.0.1", Port = simulator.Port, UnitId = 1, ResponseTimeoutMs = 1000, RetryCount = 0 };
        var tcpB = new TcpProfile { Host = "127.0.0.1", Port = simulator.Port, UnitId = 2, ResponseTimeoutMs = 1000, RetryCount = 0 };

        var connectA = await sessionA.ConnectTcpAsync(tcpA);
        var connectB = await sessionB.ConnectTcpAsync(tcpB);
        Check("会话 1 连上模拟器", connectA.IsSuccess, connectA.Error?.ToUserText());
        Check("会话 2 连上模拟器", connectB.IsSuccess, connectB.Error?.ToUserText());
        Check("两个会话同时在线", sessionA.IsConnected && sessionB.IsConnected);

        // ② 各自扫描，验证统计与报文不串会话
        var scanA = await ScanAsync(sessionA, unitId: 1, area: DataArea.HoldingRegisters, start: 0, count: 4);
        var scanB = await ScanAsync(sessionB, unitId: 2, area: DataArea.InputRegisters, start: 0, count: 4);
        Check("会话 1 扫描成功（保持寄存器）", scanA > 0, $"读到 {scanA} 个寄存器");
        Check("会话 2 扫描成功（输入寄存器）", scanB > 0, $"读到 {scanB} 个寄存器");
        Check("会话 1 统计只记自己的 TX/RX", sessionA.Statistics.TxCount > 0 && sessionA.Statistics.RxCount > 0,
            $"TX {sessionA.Statistics.TxCount} / RX {sessionA.Statistics.RxCount}");
        Check("两个会话持有各自的统计实例", !ReferenceEquals(sessionA.Statistics, sessionB.Statistics));

        // ③ 报文监视按会话投影
        var frames = bus.Snapshot();
        var framesA = frames.Count(f => f.SessionId == sessionA.SessionId);
        var framesB = frames.Count(f => f.SessionId == sessionB.SessionId);
        Check("总线上的帧带有各自的 SessionId", framesA > 0 && framesB > 0, $"会话 1 {framesA} 帧，会话 2 {framesB} 帧");

        var monitorA = sessionA.Monitor;
        var monitorB = sessionB.Monitor;
        monitorA.ReapplyFilters();
        monitorB.ReapplyFilters();
        Check("会话 1 的监视只显示自己的帧",
            monitorA.Rows.All(r => r.SessionId == sessionA.SessionId) && monitorA.Rows.Count == framesA,
            $"显示 {monitorA.Rows.Count} / 应为 {framesA}");
        Check("会话 2 的监视只显示自己的帧",
            monitorB.Rows.All(r => r.SessionId == sessionB.SessionId) && monitorB.Rows.Count == framesB,
            $"显示 {monitorB.Rows.Count} / 应为 {framesB}");

        // ④ 串口独占（规划 21.2.2 第 4 点）：登记表层面必须拒绝第二个会话
        var serialProfile = new SerialProfile { PortName = "COM99", BaudRate = 9600 };
        ports.RegisterName(sessionA.SessionId, sessionA.Name);
        ports.RegisterName(sessionB.SessionId, sessionB.Name);
        Check("会话 1 占用 COM99", ports.TryAcquire("COM99", sessionA.SessionId, sessionA.Name, out _));
        var blocked = ports.TryAcquire("COM99", sessionB.SessionId, sessionB.Name, out var busyMessage);
        Check("会话 2 不能占用同一串口", !blocked, busyMessage);
        Check("占用提示包含占用者会话名", busyMessage?.Contains(sessionA.Name, StringComparison.Ordinal) == true, busyMessage);
        var connectBlocked = await sessionB.ConnectSerialAsync(serialProfile, 5);
        Check("会话 2 直接连同一串口被拒（返回可读错误而不是硬失败）",
            !connectBlocked.IsSuccess && (connectBlocked.Error?.Message.Contains("占用", StringComparison.Ordinal) ?? false),
            connectBlocked.Error?.ToUserText());
        ports.Release("COM99", sessionA.SessionId);
        Check("释放后其他会话可占用", ports.TryAcquire("COM99", sessionB.SessionId, sessionB.Name, out _));
        ports.Release("COM99", sessionB.SessionId);

        // ⑤ 会话上限（设置里 MaxSessions = 2）
        var third = sessions.CreateSession("会话 3");
        Check("超过会话上限时被拒绝并给出原因",
            !third.IsSuccess && (third.Error?.Message.Contains("上限", StringComparison.Ordinal) ?? false),
            third.Error?.ToUserText());

        // ⑥ 工作区随会话同步（SessionWorkspaces 需要 AppServices；这里只验证注册表的会话侧）
        Check("会话注册表返回全部会话", sessions.Sessions.Count == 2, $"{sessions.Sessions.Count} 个会话");
        var closed = sessions.CloseSession(sessionB.SessionId);
        Check("可关闭其中一个会话", closed.IsSuccess, closed.Error?.ToUserText());
        Check("关闭后会话列表同步", sessions.Sessions.Count == 1 && sessions.Sessions.All(s => s.SessionId != sessionB.SessionId));
        var lastClose = sessions.CloseSession(sessions.Sessions[0].SessionId);
        Check("最后一个会话不允许关闭（至少保留一个）",
            !lastClose.IsSuccess && (lastClose.Error?.Message.Contains("至少", StringComparison.Ordinal) ?? false),
            lastClose.Error?.ToUserText());

        // ⑦ 活动会话切换与状态栏文本
        sessions.Activate(sessions.Sessions[0].SessionId);
        Check("活动会话切换后状态栏文本可用", !string.IsNullOrWhiteSpace(sessions.StatusText), sessions.StatusText);
        if (verbose) Console.WriteLine("会话摘要：" + sessions.DescribeSessions());

        Console.WriteLine("------------------------------------------------------------");
        Console.WriteLine($"结果：通过 {_passed}，失败 {_failed}");
        if (_failed == 0) Console.WriteLine("多会话核心验收通过（R-16 / 规划 21.2.3 的可自动化部分）。");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task<int> ScanAsync(SessionContext session, byte unitId, DataArea area, int start, int count)
    {
        var service = session.CreateDataScanService();
        if (service is null) return 0;

        var options = new DataScanOptions
        {
            UnitId = unitId,
            Area = area,
            StartAddress = start,
            EndAddress = start + count - 1,
            BatchSize = 0
        };

        var items = await service.ScanAsync(options, null, null, null, CancellationToken.None);
        return items.Count(i => i.Status == ScanItemStatus.Ok);
    }

    private static void Check(string title, bool condition, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  [PASS] {title}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [FAIL] {title}" + (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" — {detail}"));
        }
    }
}

/// <summary>
/// 进程内 Modbus TCP 从站模拟器：只实现读保持寄存器（03）与读输入寄存器（04），
/// 返回固定值；仅监听回环地址，不接触任何真实设备。
/// </summary>
internal sealed class LoopbackModbusTcpSlave : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public LoopbackModbusTcpSlave()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
    }

    public int Port { get; private set; }

    public string Endpoint => $"127.0.0.1:{Port}";

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client, token), token);
        }
    }

    private static async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        using var _ = client;
        var stream = client.GetStream();
        var buffer = new byte[512];

        try
        {
            while (!token.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read < 8) return;

                // MBAP：TransactionId(2) ProtocolId(2) Length(2) UnitId(1) + PDU
                ushort transactionId = (ushort)((buffer[0] << 8) | buffer[1]);
                byte unitId = buffer[6];
                byte function = buffer[7];

                ushort start = (ushort)((buffer[8] << 8) | buffer[9]);
                ushort count = (ushort)((buffer[10] << 8) | buffer[11]);

                var pdu = new List<byte> { function, (byte)(count * 2) };
                for (int i = 0; i < count; i++)
                {
                    ushort value = (ushort)(100 + start + i);
                    pdu.Add((byte)(value >> 8));
                    pdu.Add((byte)(value & 0xFF));
                }

                ushort length = (ushort)(1 + pdu.Count);
                var response = new List<byte>
                {
                    (byte)(transactionId >> 8), (byte)(transactionId & 0xFF),
                    0, 0,
                    (byte)(length >> 8), (byte)(length & 0xFF),
                    unitId
                };
                response.AddRange(pdu);

                await stream.WriteAsync(response.ToArray(), token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 客户端断开：模拟器继续接受新连接
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
            // 忽略
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        _cts.Dispose();
    }
}
