using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Comm.App.Wpf.Services;
using Comm.Capture;
using Comm.Core.Transport;
using Comm.Core.Codecs;
using Comm.Core.Frames;
using Comm.Diagnostics;
using Comm.Protocols.Modbus;
using Comm.Session;
using Comm.Settings;
using Comm.Transport;

namespace Comm.StressProbe;

/// <summary>
/// 性能与稳定性探针（M4，规划 9.2、16.3.5、16.3.4）。
/// <para>
/// 在没有界面、没有硬件的环境下量化关键指标：
/// ① 报文总线 1000 帧/s 持续接收（含 JSONL 落盘）能否跟上；
/// ② 显示缓冲 20 万条时的进程内存；
/// ③ 20 万条记录上的过滤首屏耗时；
/// ④ RTU 成帧吞吐与回环事务往返速率（给出 1000 帧/s 的余量）；
/// ⑤ 报文导出吞吐。
/// </para>
/// <para>
/// 用法：<c>Comm.StressProbe.exe [--seconds 60] [--frames 200000] [--report 报告路径] [--quick]</c>。
/// 退出码 0 = 全部阈值通过；1 = 有阈值未通过；2 = 参数错误。
/// 阈值来自规划 16.3.5（持续吞吐 1000 帧/s、20 万条过滤首屏 &lt; 1 s、进程内存 &lt; 500 MB）。
/// </para>
/// </summary>
public static class Program
{
    private const int TargetFramesPerSecond = 1000;
    private const int DisplayBufferTarget = 200_000;
    private const double FilterFirstScreenSeconds = 1.0;
    private const double MaxProcessMemoryMb = 500;

    private static readonly List<Metric> Metrics = new();
    private static int _failures;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        int seconds = ReadInt(args, "--seconds", 60);
        int frames = ReadInt(args, "--frames", DisplayBufferTarget);
        string? reportPath = ReadString(args, "--report");
        // 机器可读摘要（供 tools\Invoke-SoakTest.ps1 解析；避免解析中文控制台输出，
        // PowerShell 5.1 捕获子进程输出时用的是 OEM 代码页，中文会乱码）。
        string? jsonPath = ReadString(args, "--json");
        bool quick = args.Contains("--quick", StringComparer.OrdinalIgnoreCase);

        if (quick)
        {
            seconds = Math.Min(seconds, 5);
            frames = Math.Min(frames, 20_000);
        }

        Console.WriteLine("通讯测试工具 性能与稳定性探针（M4，规划 16.3.5）");
        Console.WriteLine($"参数：持续 {seconds} s 的 {TargetFramesPerSecond} 帧/s 注入；显示缓冲目标 {frames} 条" +
                          (quick ? "（--quick 缩短规模）" : string.Empty));
        Console.WriteLine(new string('-', 78));

        var baselineMemory = CurrentMemoryMb();
        Console.WriteLine($"基线内存：{baselineMemory:F1} MB（进程工作集）");
        Console.WriteLine();

        // ① 持续吞吐（1000 帧/s）+ JSONL 落盘
        await MeasureSustainedThroughputAsync(seconds);

        // ② 显示缓冲容量与内存
        MeasureDisplayBuffer(frames);

        // ③ 过滤首屏耗时
        await MeasureFilterFirstScreenAsync(frames);

        // ④ RTU 成帧吞吐 + 回环事务速率
        MeasureFramerThroughput();
        await MeasureLoopbackRoundTripsAsync();

        // ⑤ 报文导出吞吐
        MeasureExport(frames);

        Console.WriteLine(new string('-', 78));
        Console.WriteLine("指标汇总：");
        foreach (var metric in Metrics)
        {
            var mark = metric.Passed switch { true => "PASS", false => "FAIL", null => "INFO" };
            Console.WriteLine($"  [{mark}] {metric.Name}：{metric.Value}{metric.Unit}" +
                              (metric.Target is null ? string.Empty : $"（目标 {metric.Target}）") +
                              (metric.Detail is null ? string.Empty : $" — {metric.Detail}"));
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "结论：全部阈值通过。"
            : $"结论：{_failures} 项阈值未通过（详见上面的 FAIL 行）。");

        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            WriteReport(reportPath!, seconds, frames, baselineMemory);
            Console.WriteLine($"报告已写入：{System.IO.Path.GetFullPath(reportPath!)}");
        }

        if (!string.IsNullOrWhiteSpace(jsonPath))
        {
            WriteJsonSummary(jsonPath!, seconds, frames, baselineMemory);
            Console.WriteLine($"JSON 摘要已写入：{System.IO.Path.GetFullPath(jsonPath!)}");
        }

        return _failures == 0 ? 0 : 1;
    }

    // -------------------------------------------------------------------------------------
    // ① 持续吞吐
    // -------------------------------------------------------------------------------------

    private static async Task MeasureSustainedThroughputAsync(int seconds)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "stress-output");
        Directory.CreateDirectory(directory);
        var capturePath = Path.Combine(directory, $"frames-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");

        await using var writer = new FrameFileWriter(maxFileSizeMb: 256);
        var writerError = writer.Start(capturePath);
        await using var bus = new FrameBus(DisplayBufferTarget, writer);

        long produced = 0;
        long droppedByDisplay = 0;
        var stopwatch = Stopwatch.StartNew();
        var memoryBefore = CurrentMemoryMb();

        // 目标 500 µs 一帧（1000 帧/s），用较小的批次精确控制节奏。
        const int batchSize = 10;
        double intervalMs = 1000.0 / TargetFramesPerSecond;
        var next = TimeSpan.Zero;

        while (stopwatch.Elapsed.TotalSeconds < seconds)
        {
            for (int i = 0; i < batchSize; i++)
            {
                bus.OnFrame(CreateFrame(produced, producer: 0));
                produced++;
            }

            next += TimeSpan.FromMilliseconds(intervalMs * batchSize);
            var remaining = next - stopwatch.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining).ConfigureAwait(false);
            }
            else if (remaining < TimeSpan.FromMilliseconds(-200))
            {
                // 落后超过 200 ms：不再追赶，避免把“落后”伪造成“高吞吐”
                next = stopwatch.Elapsed;
            }
        }

        stopwatch.Stop();
        double achieved = produced / stopwatch.Elapsed.TotalSeconds;
        droppedByDisplay = bus.DroppedFromDisplay;

        // 落盘排空：先停止接收，再等文件行数追平（超时后按实际行数判定“记录是否跟上”）
        writer.Stop();
        var flushWatch = Stopwatch.StartNew();
        while (writer.WrittenCount < produced && flushWatch.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(100).ConfigureAwait(false);
        }

        long written = writer.WrittenCount;

        double memoryDelta = CurrentMemoryMb() - memoryBefore;

        // 节拍器实测速率：用 Task.Delay 控制节奏，受 Windows 计时器粒度（约 15.6 ms）影响，
        // 因此只作为参考值（INFO），不作为通过/失败判据。
        Add("注入节拍速率（参考）", achieved, "帧/s", null, null,
            $"注入 {produced} 帧，用时 {stopwatch.Elapsed.TotalSeconds:F1} s；节拍精度受系统计时器粒度限制");

        // 落盘记录完整性：只要有 1 条丢失就是缺陷（规划 13.5：落盘不丢）
        Add("落盘记录完整性", written, "条", $"= {produced}", written == produced,
            writerError is null ? $"文件 {System.IO.Path.GetFileName(capturePath)}" : $"写入器错误：{writerError}");
        Add("显示缓冲丢弃", droppedByDisplay, "条", "不丢盘", true, "显示层可丢，落盘不丢（规划 13.5）");
        Add("持续吞吐期间内存增量", memoryDelta, "MB", null, null, "含显示缓冲与落盘队列");

        // 总线 + 落盘的处理能力：不限速地灌入固定条数，测真实上限（这才是“能否支撑 1000 帧/s”的判据）。
        MeasureBusAndWriterCapacity(Math.Max(20_000, TargetFramesPerSecond * 30));
        Console.WriteLine();
    }

    /// <summary>
    /// 不限速灌入固定条数，测量“帧总线 + JSONL 落盘”的真实处理能力。
    /// 这是判断“能否支撑 1000 帧/s”的硬指标（规划 16.3.5）；节拍器速率只是参考值。
    /// </summary>
    private static void MeasureBusAndWriterCapacity(int frames)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "stress-output");
        Directory.CreateDirectory(directory);
        var capturePath = Path.Combine(directory, $"capacity-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");

        var writer = new FrameFileWriter(maxFileSizeMb: 256);
        var writerError = writer.Start(capturePath);
        var bus = new FrameBus(DisplayBufferTarget, writer);

        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++) bus.OnFrame(CreateFrame(i, producer: 2));
        stopwatch.Stop();

        double perSecond = frames / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);

        writer.Stop();
        var flushWatch = Stopwatch.StartNew();
        while (writer.WrittenCount < frames && flushWatch.Elapsed < TimeSpan.FromSeconds(60))
        {
            Thread.Sleep(50);
        }

        long written = writer.WrittenCount;
        long busTotal = bus.TotalCount;

        Add("总线+落盘处理能力", perSecond, "帧/s", $"≥ {TargetFramesPerSecond}", perSecond >= TargetFramesPerSecond,
            $"{frames} 帧灌入 {stopwatch.Elapsed.TotalSeconds:F2} s（落盘 {written} 条，总线 {busTotal} 条）");
        Add("处理能力测试落盘完整性", written, "条", $"= {frames}", written == frames,
            writerError is null ? System.IO.Path.GetFileName(capturePath) : $"写入器错误：{writerError}");

        // 关闭并清理（探针产物不入库）
        var dispose = writer.DisposeAsync();
        dispose.AsTask().GetAwaiter().GetResult();
        try
        {
            foreach (var file in Directory.GetFiles(directory, "capacity-*.jsonl")) File.Delete(file);
        }
        catch (Exception)
        {
            // 清理失败不影响结论
        }
    }

    // -------------------------------------------------------------------------------------
    // ② 显示缓冲
    // -------------------------------------------------------------------------------------

    private static void MeasureDisplayBuffer(int frames)
    {
        var memoryBefore = CurrentMemoryMb();
        var bus = new FrameBus(Math.Max(frames, 1000));
        var stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < frames; i++) bus.OnFrame(CreateFrame(i, producer: 1));

        stopwatch.Stop();
        var retained = bus.Snapshot().Count;
        double memoryDelta = CurrentMemoryMb() - memoryBefore;
        double memoryTotal = CurrentMemoryMb();

        Add("显示缓冲写入耗时", stopwatch.Elapsed.TotalSeconds * 1000, "ms", null, null, $"{frames} 条");
        Add("显示缓冲保留条数", retained, "条", $"= {frames}", retained == frames);
        Add("进程内存（含缓冲）", memoryTotal, "MB", $"< {MaxProcessMemoryMb}", memoryTotal < MaxProcessMemoryMb,
            $"缓冲增量 {memoryDelta:F1} MB");

        Console.WriteLine();
    }

    // -------------------------------------------------------------------------------------
    // ③ 过滤首屏
    // -------------------------------------------------------------------------------------

    private static async Task MeasureFilterFirstScreenAsync(int frames)
    {
        var settings = new AppSettings { Monitor = new MonitorSettings { UiCapacity = frames } };
        var bus = new FrameBus(Math.Max(frames, 1000));

        for (int i = 0; i < frames; i++)
        {
            // 一半帧属于“其他会话”，用于验证按会话投影的过滤成本
            bus.OnFrame(CreateFrame(i, producer: i % 2));
        }

        await using var log = new LogGate(new LogOptions { Enabled = false });
        var monitor = new MonitorService(bus, log, settings, sessionIdFilter: "probe-0");

        var stopwatch = Stopwatch.StartNew();
        monitor.ReapplyFilters();
        stopwatch.Stop();

        double seconds = stopwatch.Elapsed.TotalSeconds;
        Add("过滤首屏耗时（按会话）", seconds * 1000, "ms", $"< {FilterFirstScreenSeconds * 1000} ms",
            seconds < FilterFirstScreenSeconds,
            $"显示 {monitor.Rows.Count} / 总线 {bus.TotalCount} 条");

        Console.WriteLine();
    }

    // -------------------------------------------------------------------------------------
    // ④ 成帧吞吐与回环往返
    // -------------------------------------------------------------------------------------

    private static void MeasureFramerThroughput()
    {
        var codec = new ModbusRtuCodec();
        // 用 Monitor 角色 + 长度可预测的读响应帧（01 03 14 <20 字节> CRC）：
        // 主站角色会等待“请求对应的响应”，用它测成帧吞吐会一直等一下帧，测不出上限。
        var framer = codec.CreateFramer(FramerRole.Monitor);
        var responseBody = new List<byte> { 0x03, 0x14 };
        for (int i = 0; i < 20; i++) responseBody.Add((byte)i);
        var adu = codec.BuildAdu(responseBody.ToArray(), 1);

        const int iterations = 20_000;
        var stopwatch = Stopwatch.StartNew();
        int produced = 0;

        for (int i = 0; i < iterations; i++)
        {
            produced += framer.Feed(adu, Stopwatch.GetTimestamp()).Count;
        }

        stopwatch.Stop();
        double perSecond = iterations / stopwatch.Elapsed.TotalSeconds;

        Add("RTU 成帧吞吐", perSecond, "帧/s", $">> {TargetFramesPerSecond}", perSecond > TargetFramesPerSecond * 10,
            $"解析出 {produced} 帧");
        Console.WriteLine();
    }

    private static async Task MeasureLoopbackRoundTripsAsync()
    {
        var slave = new LoopbackSlave();
        var codec = new ModbusRtuCodec();
        await using var channel = new LoopbackChannel(slave.Respond, responseDelayMs: 0);
        var options = new SessionOptions { ResponseTimeoutMs = 200, RetryCount = 0, TurnaroundDelayMs = 0, SuppressEcho = false };
        await using var session = new ModbusSession(channel, codec, options);
        await session.ConnectAsync();

        const int iterations = 2000;
        var stopwatch = Stopwatch.StartNew();
        int ok = 0;

        for (int i = 0; i < iterations; i++)
        {
            var result = await session.ExecuteAsync(ModbusRequest.Scan(1, ModbusRequests.Read(DataArea.HoldingRegisters, 0, 10)),
                CancellationToken.None).ConfigureAwait(false);
            if (result.IsSuccess) ok++;
        }

        stopwatch.Stop();
        double perSecond = ok / stopwatch.Elapsed.TotalSeconds;

        Add("回环事务往返", perSecond, "次/s", null, null, $"成功 {ok}/{iterations}（软件上限参考值）");
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------------------
    // ⑤ 导出吞吐
    // -------------------------------------------------------------------------------------

    private static void MeasureExport(int frames)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "stress-output");
        Directory.CreateDirectory(directory);

        var records = new List<FrameRecord>(frames);
        for (int i = 0; i < frames; i++) records.Add(CreateFrame(i, producer: 0));

        var csvPath = Path.Combine(directory, "export-probe.csv");
        var stopwatch = Stopwatch.StartNew();
        var error = FrameExporter.ExportCsv(csvPath, records);
        stopwatch.Stop();

        Add("导出 CSV", stopwatch.Elapsed.TotalSeconds * 1000, "ms", null, null,
            string.IsNullOrEmpty(error)
                ? $"{frames} 条 → {System.IO.Path.GetFileName(csvPath)}"
                : error);

        try
        {
            File.Delete(csvPath);
        }
        catch (Exception)
        {
            // 清理失败不影响结论
        }

        Console.WriteLine();
    }

    // -------------------------------------------------------------------------------------
    // 工具
    // -------------------------------------------------------------------------------------

    private static FrameRecord CreateFrame(long index, int producer) => new()
    {
        Seq = index,
        SessionId = $"probe-{producer}",
        Direction = index % 2 == 0 ? FrameDirection.Tx : FrameDirection.Rx,
        Timestamp = DateTimeOffset.Now,
        MonotonicTicks = Stopwatch.GetTimestamp(),
        RawBytes = index % 3 == 0
            ? new byte[] { 0x01, 0x03, 0x02, 0x00, 0x2A, 0x39, 0x8B }
            : new byte[] { 0x01, 0x03, 0x14, 0x00, 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04 },
        UnitId = 1,
        FunctionCode = 0x03,
        Summary = "压力探针构造帧"
    };

    private static double CurrentMemoryMb()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.WorkingSet64 / 1024.0 / 1024.0;
    }

    private static void Add(string name, double value, string unit, string? target, bool? passed, string? detail = null)
    {
        var rounded = unit is "ms" or "MB" ? Math.Round(value, 2) : Math.Round(value, 1);
        Metrics.Add(new Metric(name, rounded, unit, target, passed, detail));
        if (passed == false) _failures++;

        var mark = passed switch { true => "PASS", false => "FAIL", null => "INFO" };
        Console.WriteLine($"  [{mark}] {name}：{rounded.ToString(CultureInfo.InvariantCulture)} {unit}" +
                          (target is null ? string.Empty : $"（目标 {target}）") +
                          (detail is null ? string.Empty : $" — {detail}"));
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value) ? value : fallback;
    }

    private static string? ReadString(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void WriteReport(string path, int seconds, int frames, double baselineMemory)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 性能与稳定性探针报告");
        builder.AppendLine();
        builder.AppendLine($"- 生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"- 机器：{Environment.MachineName}（{Environment.OSVersion.VersionString}，" +
                           $"{(Environment.Is64BitProcess ? "x64" : "x86")}）");
        builder.AppendLine($"- 运行时：.NET {Environment.Version}");
        builder.AppendLine($"- 参数：持续 {seconds} s @ {TargetFramesPerSecond} 帧/s；显示缓冲 {frames} 条");
        builder.AppendLine($"- 基线内存：{baselineMemory:F1} MB");
        builder.AppendLine();
        builder.AppendLine("| 指标 | 数值 | 目标 | 结论 | 说明 |");
        builder.AppendLine("|---|---|---|---|---|");
        foreach (var metric in Metrics)
        {
            var mark = metric.Passed switch { true => "通过", false => "未通过", null => "参考" };
            builder.AppendLine($"| {metric.Name} | {metric.Value} {metric.Unit} | {metric.Target ?? "-"} | {mark} | {metric.Detail ?? "-"} |");
        }

        builder.AppendLine();
        builder.AppendLine(_failures == 0 ? "**结论：全部阈值通过。**" : $"**结论：{_failures} 项阈值未通过。**");
        builder.AppendLine();
        builder.AppendLine("> 说明：这些数字是**软件上限参考值**（回环传输、无硬件、无界面渲染）。");
        builder.AppendLine("> 现场硬件相关的结论（真实适配器吞吐、1000 帧/s 下的界面可操作性、24 h 稳定性）仍需在目标机上用");
        builder.AppendLine("> `tools\\Invoke-SoakTest.ps1` 与 `docs/hardware-compatibility-matrix-v1.md` 的清单回填。");

        File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
    }

    private sealed record Metric(string Name, double Value, string Unit, string? Target, bool? Passed, string? Detail);

    /// <summary>写 JSON 摘要（机器可读；字段名稳定，供自动化脚本消费）。</summary>
    private static void WriteJsonSummary(string path, int seconds, int frames, double baselineMemory)
    {
        var payload = new
        {
            probe = "Comm.StressProbe",
            generatedAt = DateTimeOffset.Now,
            machine = Environment.MachineName,
            os = Environment.OSVersion.VersionString,
            runtime = Environment.Version.ToString(),
            parameters = new { seconds, frames },
            baselineMemoryMb = Math.Round(baselineMemory, 1),
            processMemoryMb = Math.Round(CurrentMemoryMb(), 1),
            failures = _failures,
            passed = _failures == 0,
            metrics = Metrics.Select(m => new
            {
                name = m.Name,
                value = m.Value,
                unit = m.Unit,
                target = m.Target,
                // null = 参考值（INFO），true/false = 阈值通过/未通过
                passed = m.Passed,
                detail = m.Detail
            }).ToArray()
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
    }

    /// <summary>极简从站响应器（只应答 03/04 读请求），仅用于回环往返测量。</summary>
    private sealed class LoopbackSlave
    {
        public byte[]? Respond(byte[] request)
        {
            if (request.Length < 8) return null;
            var unitId = request[0];
            var function = request[1];
            if (function is not (0x03 or 0x04)) return null;

            // RTU 帧布局：地址(1) 功能码(1) 起始地址(2) 数量(2) CRC(2)
            ushort quantity = (ushort)((request[4] << 8) | request[5]);
            if (quantity is 0 or > 125) return null;
            var pdu = new List<byte> { function, (byte)(quantity * 2) };
            for (int i = 0; i < quantity; i++)
            {
                pdu.Add(0x00);
                pdu.Add((byte)(i & 0xFF));
            }

            var codec = new ModbusRtuCodec();
            return codec.BuildAdu(pdu.ToArray(), unitId);
        }
    }
}
