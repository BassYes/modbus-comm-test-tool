using System.Globalization;
using System.Text;
using Comm.Core;
using Comm.Core.Codecs;
using Comm.Core.Frames;
using Comm.Core.Transport;
using Comm.Protocols.Modbus;
using Comm.Transport;

namespace Comm.HilProbe;

/// <summary>
/// 真实硬件回归探针（M2.5 第 6 项，规划 22.6.4 / v0.1 交付说明 4.3）。
/// <para>
/// 它不依赖界面，直接打开串口、发送一次 Modbus RTU 只读请求并按字节记录到达时间，用来回答三个问题：
/// 1. **回显**：USB-RS485 适配器是否把请求原样回显（决定是否需要回显抑制）；
/// 2. **拆帧**：收到的字节能否被 <see cref="RtuFramer"/> 正确成帧，最大字节间隔是多少；
/// 3. **静默阈值**：按 <see cref="RtuTiming"/> 公式算出的阈值是否高于实测最大字节间隔。
/// </para>
/// <para>结果同时打印并写入报告文件，供回填 docs/hardware-compatibility-matrix-v1.md。</para>
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            ListPorts();
            return 0;
        }

        var port = GetArg(args, "--port");
        if (string.IsNullOrWhiteSpace(port))
        {
            Console.Error.WriteLine("缺少 --port（例如 --port COM5）。");
            ListPorts();
            return 2;
        }

        var unit = byte.Parse(GetArg(args, "--unit") ?? "1", CultureInfo.InvariantCulture);
        var address = ushort.Parse(GetArg(args, "--address") ?? "0", CultureInfo.InvariantCulture);
        var quantity = ushort.Parse(GetArg(args, "--quantity") ?? "1", CultureInfo.InvariantCulture);
        var timeoutMs = int.Parse(GetArg(args, "--timeout") ?? "1000", CultureInfo.InvariantCulture);
        var repeats = int.Parse(GetArg(args, "--repeats") ?? "3", CultureInfo.InvariantCulture);
        var bauds = (GetArg(args, "--baud") ?? "9600,19200").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var parities = (GetArg(args, "--parity") ?? "N,E").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var stopBitsList = (GetArg(args, "--stop") ?? "1").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var outputPath = GetArg(args, "--out") ?? $"hil-probe-{DateTime.Now:yyyyMMdd-HHmmss}.txt";

        var report = new StringBuilder();
        report.AppendLine("通讯测试工具 真实硬件回归探针（HIL Probe）");
        report.AppendLine($"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"串口：{port}；从站地址：{unit}；请求：03 读保持寄存器 起始 {address} 数量 {quantity}；每次超时 {timeoutMs} ms；每组合重复 {repeats} 次");
        report.AppendLine($"适配器驱动提示：{SerialChannel.SerialOpenHint(port)}");
        report.AppendLine(new string('=', 100));

        foreach (var baudText in bauds)
        {
            var baud = int.Parse(baudText.Trim(), CultureInfo.InvariantCulture);
            foreach (var parityText in parities)
            {
                var parity = ParseParity(parityText);
                foreach (var stopText in stopBitsList)
                {
                    var stopBits = stopText.Trim() == "2" ? SerialStopBits.Two : SerialStopBits.One;
                    var settings = new SerialSettings
                    {
                        PortName = port!,
                        BaudRate = baud,
                        DataBits = 8,
                        Parity = parity,
                        StopBits = stopBits,
                        ReadTimeoutMs = Math.Max(100, timeoutMs)
                    };

                    var block = await ProbeAsync(settings, unit, address, quantity, timeoutMs, repeats)
                        .ConfigureAwait(false);
                    report.Append(block);
                    Console.Write(block);
                }
            }
        }

        report.AppendLine(new string('=', 100));
        report.AppendLine("结论填写提示（回填 docs/hardware-compatibility-matrix-v1.md）：");
        report.AppendLine("- 若“回显=是”，请在设备扫描/数据扫描中保持“回显抑制”开启；");
        report.AppendLine("- 若“最大字节间隔”接近或超过该波特率的静默阈值，请如实记录（可能需要在规划中提高阈值）；");
        report.AppendLine("- “可用”组合即为该适配器 + 该设备在当前接线下的工作参数。");

        try
        {
            File.WriteAllText(outputPath, report.ToString(), new UTF8Encoding(true));
            Console.WriteLine();
            Console.WriteLine($"报告已写入：{Path.GetFullPath(outputPath)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"写入报告失败：{ex.Message}");
        }

        return 0;
    }

    private static async Task<string> ProbeAsync(SerialSettings settings, byte unit, ushort address, ushort quantity,
        int timeoutMs, int repeats)
    {
        var sb = new StringBuilder();
        int silence = RtuTiming.ComputeSilenceThresholdMs(settings.BaudRate);
        int effectiveTimeout = RtuTiming.EffectiveResponseTimeoutMs(timeoutMs, settings.BaudRate);

        sb.AppendLine();
        sb.AppendLine($"---- {settings.BaudRate}-{settings.DataBits}-{SerialSettings.ParityCode(settings.Parity)}-" +
                      $"{SerialSettings.StopBitsCode(settings.StopBits)} ----");
        sb.AppendLine($"静默阈值（公式）：{silence} ms；静默阈值（旧实现固定 5 ms）；响应超时（下限校正后）：{effectiveTimeout} ms");

        var codec = new ModbusRtuCodec(silence);
        var pdu = ModbusRequests.Read(DataArea.HoldingRegisters, address, quantity);
        var request = codec.BuildAdu(pdu.ToBytes(), unit);
        sb.AppendLine($"请求：{Hex(request)}（{request.Length} 字节）");

        var channel = new SerialChannel(settings);
        try
        {
            await channel.OpenAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"打开失败：{ex.Message}");
            sb.AppendLine("结论：不可用（串口无法打开）");
            return sb.ToString();
        }

        try
        {
            for (int round = 1; round <= repeats; round++)
            {
                var framer = new RtuFramer(FramerRole.Master, silence);
                framer.SetExpectation(unit, pdu.Function);

                var chunks = new List<RxChunk>();
                using var cts = new CancellationTokenSource(effectiveTimeout);

                var rxTask = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var chunk in channel.ReadAsync(cts.Token).ConfigureAwait(false))
                        {
                            chunks.Add(chunk);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                });

                await channel.WriteAsync(request).ConfigureAwait(false);
                try
                {
                    await Task.Delay(effectiveTimeout, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                await cts.CancelAsync().ConfigureAwait(false);
                try
                {
                    await rxTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 读取协程未及时退出不影响结论
                }

                var bytes = chunks.SelectMany(c => c.Data).ToArray();
                var gaps = ComputeGaps(chunks);
                double maxGap = gaps.Count == 0 ? 0 : gaps.Max();
                double span = chunks.Count < 2 ? 0 : Clock.MillisecondsBetween(chunks[0].MonotonicTicks, chunks[^1].MonotonicTicks);

                var frames = new List<FrameCandidate>();
                foreach (var chunk in chunks)
                {
                    frames.AddRange(framer.Feed(chunk.Data, chunk.MonotonicTicks));
                }
                frames.AddRange(framer.OnTimer(Clock.MonotonicTicks));

                bool echo = bytes.Length >= request.Length && bytes.AsSpan(0, request.Length).SequenceEqual(request);
                var body = echo ? bytes[request.Length..] : bytes;

                var ok = frames.FirstOrDefault(f => f.Status == FrameStatus.Valid);
                var checksum = frames.Count(f => f.Status == FrameStatus.ChecksumError);

                sb.AppendLine();
                sb.AppendLine($"  第 {round} 次：");
                sb.AppendLine($"    收到 {bytes.Length} 字节 / {chunks.Count} 个字节块；{Hex(bytes)}");
                sb.AppendLine($"    回显请求：{(echo ? "是（请求被原样回显）" : "否")}；响应部分 {body.Length} 字节");
                sb.AppendLine($"    最大字节间隔：{maxGap.ToString("F2", CultureInfo.InvariantCulture)} ms；" +
                              $"整段跨越 {span.ToString("F2", CultureInfo.InvariantCulture)} ms；" +
                              $"（阈值 {silence} ms → {(maxGap < silence ? "安全" : "偏紧，需复核")}）");
                sb.AppendLine($"    成帧结果：有效帧 {(ok is null ? 0 : 1)} 个；校验错误 {checksum} 个；" +
                              $"{(ok is null ? "未得到有效帧" : "响应 " + Hex(ok.Bytes))}");

                if (ok is not null)
                {
                    var parsed = PduParser.Parse(ModbusPdu.Parse(SlicePdu(ok.Bytes, codec.ChecksumLength)), false);
                    sb.AppendLine($"    解析：{parsed.Describe()}");
                    sb.AppendLine($"    结论：可用（{settings.BaudRate}-{SerialSettings.ParityCode(settings.Parity)}-" +
                                  $"{SerialSettings.StopBitsCode(settings.StopBits)}）；" +
                                  $"往返 {span.ToString("F2", CultureInfo.InvariantCulture)} ms" +
                                  (echo ? "；需要回显抑制" : "；无需回显抑制"));
                    break;
                }

                if (echo && body.Length == 0)
                {
                    sb.AppendLine("    结论：只收到回显、没有响应（检查从站地址、接线 A/B、是否已上电）");
                }
            }
        }
        finally
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        return sb.ToString();
    }

    private static byte[] SlicePdu(byte[] adu, int checksumLength)
    {
        int end = Math.Max(0, adu.Length - checksumLength);
        return adu.Length <= 1 || end <= 1 ? Array.Empty<byte>() : adu[1..end];
    }

    private static List<double> ComputeGaps(IReadOnlyList<RxChunk> chunks)
    {
        var gaps = new List<double>();
        for (int i = 1; i < chunks.Count; i++)
        {
            gaps.Add(Clock.MillisecondsBetween(chunks[i - 1].MonotonicTicks, chunks[i].MonotonicTicks));
        }
        return gaps;
    }

    private static SerialParity ParseParity(string text) => text.Trim().ToUpperInvariant() switch
    {
        "E" => SerialParity.Even,
        "O" or "ODD" => SerialParity.Odd,
        _ => SerialParity.None
    };

    private static string Hex(byte[] bytes) => bytes.Length == 0
        ? "(空)"
        : string.Join(' ', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }

    private static void ListPorts()
    {
        var ports = SerialChannel.GetPortNames();
        Console.WriteLine();
        Console.WriteLine(ports.Count == 0
            ? "当前未检测到串口（请插入 USB-RS485 适配器并安装驱动）。"
            : "检测到的串口：" + string.Join(", ", ports));
        foreach (var port in ports) Console.WriteLine($"  {port}：{SerialChannel.SerialOpenHint(port)}");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
        通讯测试工具 真实硬件回归探针（HIL Probe）

        用法：
          dotnet run --project tools/Comm.HilProbe -- --port COM5 [选项]

        选项：
          --port     串口号（必填，例如 COM5；用不带参数运行可罗列当前串口）
          --unit     从站地址，默认 1
          --address  起始寄存器地址，默认 0
          --quantity 读取寄存器数量，默认 1
          --baud     候选波特率列表，默认 9600,19200
          --parity   候选校验位列表，默认 N,E（可选 N/E/O）
          --stop     候选停止位列表，默认 1（可选 1/2）
          --timeout  单次响应超时 ms，默认 1000
          --repeats  每个组合最多重复次数，默认 3
          --out      报告输出文件，默认 hil-probe-<时间>.txt

        示例：
          dotnet run --project tools/Comm.HilProbe -- --port COM5 --unit 5 --baud 9600,19200,38400 --parity N,E,O
        """);
    }
}
