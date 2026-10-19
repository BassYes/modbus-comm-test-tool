using System.Runtime.Versioning;
using Comm.Capture;
using Comm.Core;
using Comm.Core.Codecs;
using Comm.Core.Errors;
using Comm.Core.Frames;
using Comm.Diagnostics;
using Comm.Platform.Windows;
using Comm.Protocols.Modbus;
using Comm.Scanning;
using Comm.Session;
using Comm.Settings;
using Comm.TestSupport;
using Comm.Transport;
using Xunit;

namespace Comm.Security.Tests;

/// <summary>
/// 安全与高风险回归S规划 16.3.9 的 SR-1 ~ SR-7，每次发布必跑）。
/// <para>
/// 每个用例的注释里标注对应的 SR 编号；<c>内部文档</c> 是同一张表的文档版本
/// S含无法自动验证、需要人工执行的步骤）。
/// </para>
/// </summary>
// 网卡配置相关用例只适用于 WindowsSComm.Platform.Windows 标注了 SupportedOSPlatform）。
[SupportedOSPlatform("windows")]
public class SecurityRegressionTests
{
    // -------------------------------------------------------------------------------------
    // 公共脚手架
    // -------------------------------------------------------------------------------------

    private static (ModbusSession Session, ModbusSlaveSimulator Slave) CreateRtuSession(
        ModbusSlaveSimulator? slave = null, int responseTimeoutMs = 100)
    {
        slave ??= new ModbusSlaveSimulator();
        var codec = new ModbusRtuCodec();
        var channel = new LoopbackChannel(slave.CreateResponder(codec), responseDelayMs: 1);
        var options = new SessionOptions
        {
            ResponseTimeoutMs = responseTimeoutMs,
            RetryCount = 0,
            TurnaroundDelayMs = 1,
            IsSerial = true,
            SuppressEcho = false
        };

        return (new ModbusSession(channel, codec, options), slave);
    }

    /// <summary>断言模拟器收到的所有请求都是只读功能码SSR-1 的核心断言）。</summary>
    private static void AssertOnlyReadRequests(ModbusSlaveSimulator slave, string what)
    {
        Assert.NotEmpty(slave.ReceivedRequests);
        var writes = slave.ReceivedRequests.Where(r => Fn.IsWrite(r.Function)).Select(r => $"0x{r.Function:X2}").Distinct().ToArray();
        Assert.True(writes.Length == 0, $"{what} 产生了写功能码请求：{string.Join(", ", writes)}");
        Assert.All(slave.ReceivedRequests, r => Assert.True(Fn.IsRead(r.Function), $"{what} 出现了非只读功能码 0x{r.Function:X2}"));
    }

    // =====================================================================================
    // SR-1：全部扫描功能抓取实际发出的字节，断言不含写功能码
    // =====================================================================================

    [Fact]
    public async Task SR1_Device_scan_uses_only_read_function_codes()
    {
        var slave = new ModbusSlaveSimulator { UnitId = 3 };
        var (session, _) = CreateRtuSession(slave);
        await session.ConnectAsync();

        await new DeviceScanService(session).ScanAsync(new DeviceScanOptions
        {
            StartAddress = 1,
            EndAddress = 6,
            ResponseTimeoutMs = 40,
            IntervalMs = 0,
            TryReadDeviceIdentification = false
        }, null, null, null, CancellationToken.None);

        AssertOnlyReadRequests(slave, "串口设备扫描");
        await session.DisposeAsync();
    }

    [Fact]
    public async Task SR1_Device_scan_in_manual_mode_refuses_write_templates()
    {
        var slave = new ModbusSlaveSimulator { UnitId = 1 };
        var (session, _) = CreateRtuSession(slave);
        await session.ConnectAsync();

        // 手动模式选中写功能码：参数校验必须先拦住S连请求都不该发出）
        var options = new DeviceScanOptions
        {
            Mode = DeviceProbeMode.Manual,
            ManualProbeFunction = Fn.WriteMultipleRegisters,
            ManualProbeAddress = 0,
            ManualProbeQuantity = 1,
            StartAddress = 1,
            EndAddress = 3,
            ResponseTimeoutMs = 40,
            IntervalMs = 0
        };

        Assert.False(DeviceScanOptions.Validate(options, out var error));
        Assert.Equal(ErrorCodes.ValidationScanWriteBlocked, error!.Code);

        // 即使绕过界面直接调用服务，服务层也必须拒绝S抛 CommException，且不发任何请求）
        await Assert.ThrowsAsync<CommException>(async () =>
            await new DeviceScanService(session).ScanAsync(options, null, null, null, CancellationToken.None));

        Assert.Empty(slave.ReceivedRequests);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task SR1_Unit_id_scan_uses_only_read_function_codes_and_never_broadcasts()
    {
        var slave = new ModbusSlaveSimulator { UnitId = 2 };
        var codec = new ModbusRtuCodec();
        var slaveResponder = slave.CreateResponder(codec);
        var observedUnitIds = new List<byte>();
        var channel = new LoopbackChannel(request =>
        {
            if (request.Length >= 1) observedUnitIds.Add(request[0]);
            return slaveResponder(request);
        }, responseDelayMs: 1);
        var session = new ModbusSession(channel, codec,
            new SessionOptions { ResponseTimeoutMs = 40, RetryCount = 0, TurnaroundDelayMs = 1, SuppressEcho = false });
        await session.ConnectAsync();

        await new UnitIdScanService(session).ScanAsync(new UnitIdScanOptions
        {
            StartUnitId = 1,
            EndUnitId = 5,
            ResponseTimeoutMs = 60,
            IntervalMs = 0,
            TryReadDeviceIdentification = false
        }, null, null, null, CancellationToken.None);

        // SR-1：只读功能码；并且广播地址 0 必须被参数校验拒绝S这里逐条确认请求里没有 unit 0）
        AssertOnlyReadRequests(slave, "TCP Unit ID 扫描");
        Assert.DoesNotContain((byte)0, observedUnitIds);

        var broadcast = new UnitIdScanOptions { StartUnitId = 0, EndUnitId = 247 };
        Assert.False(UnitIdScanOptions.Validate(broadcast, out var error));
        Assert.Equal(ErrorCodes.ValidationAddressRange, error!.Code);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task SR1_Data_scan_uses_only_read_function_codes()
    {
        var slave = new ModbusSlaveSimulator { UnitId = 1 };
        for (ushort i = 0; i < 8; i++) slave.HoldingRegisters[i] = i;
        var (session, _) = CreateRtuSession(slave);
        await session.ConnectAsync();

        await new DataScanService(session).ScanAsync(new DataScanOptions
        {
            UnitId = 1,
            Area = DataArea.HoldingRegisters,
            StartAddress = 0,
            EndAddress = 7,
            ResponseTimeoutMs = 60,
            IntervalMs = 0
        }, null, null, null, CancellationToken.None);

        AssertOnlyReadRequests(slave, "数据区扫描");
        await session.DisposeAsync();
    }

    [Fact]
    public async Task SR1_Register_locator_uses_only_read_function_codes()
    {
        var slave = new ModbusSlaveSimulator { UnitId = 1 };
        for (ushort i = 0; i < 16; i++) slave.HoldingRegisters[i] = (ushort)(100 + i);
        var (session, _) = CreateRtuSession(slave);
        await session.ConnectAsync();

        var locator = new RegisterLocator(session);
        await locator.LocateAsync(new RegisterLocatorOptions
        {
            UnitId = 1,
            Area = DataArea.HoldingRegisters,
            StartAddress = 0,
            EndAddress = 15,
            Mode = LocatorMode.Exact,
            Target = 105,
            ResponseTimeoutMs = 60,
            IntervalMs = 0
        }, null, null, null, CancellationToken.None);

        AssertOnlyReadRequests(slave, "寄存器定位");
        await session.DisposeAsync();
    }

    [Fact]
    public async Task SR1_Watch_table_refresh_uses_only_read_function_codes()
    {
        var slave = new ModbusSlaveSimulator { UnitId = 1 };
        slave.HoldingRegisters[5] = 42;
        var (session, _) = CreateRtuSession(slave);
        await session.ConnectAsync();

        var service = new WatchTableService(session);
        service.Add(new RegisterPoint { UnitId = 1, Area = DataArea.HoldingRegisters, Address = 5, Enabled = true });
        await service.RefreshOnceAsync(CancellationToken.None);

        AssertOnlyReadRequests(slave, "监视表周期读取");
        await session.DisposeAsync();
    }

    [Fact]
    public async Task SR1_Parameter_scan_stage_a_uses_only_read_function_codes()
    {
        // R-11 未知参数扫描同样属于“扫描类任务”，必须只读S阶段 A 固定 03）
        var slave = new ModbusSlaveSimulator { UnitId = 1 };
        var (session, _) = CreateRtuSession(slave);
        await session.ConnectAsync();

        await new DeviceScanService(session).ProbeAddressAsync(1, new DeviceScanOptions
        {
            Mode = DeviceProbeMode.Manual,
            ManualProbeFunction = Fn.ReadHoldingRegisters,
            ManualProbeAddress = 0,
            ManualProbeQuantity = 1,
            StartAddress = 1,
            EndAddress = 1,
            ResponseTimeoutMs = 40,
            IntervalMs = 0,
            TryReadDeviceIdentification = false
        }, CancellationToken.None);

        AssertOnlyReadRequests(slave, "参数扫描阶段 A 探测");
        await session.DisposeAsync();
    }

    // =====================================================================================
    // SR-2 / SR-3：写请求必须二次确认；广播写必须专项确认
    // =====================================================================================

    [Fact]
    public async Task SR2_Write_request_without_confirmation_is_rejected_and_nothing_is_sent()
    {
        var slave = new ModbusSlaveSimulator { UnitId = 1 };
        var (session, _) = CreateRtuSession(slave);
        await session.ConnectAsync();

        // 事务层守卫：未确认的写请求必须被拒绝SVAL-4004），且不产生任何 TX
        var request = new ModbusRequest(1, ModbusRequests.WriteSingleRegister(0, 1234), RequestPriority.Interactive,
            Confirmed: false);
        var result = await session.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ExchangeStatus.Rejected, result.Status);
        Assert.Equal(ErrorCodes.ValidationWriteNotConfirmed, result.Error!.Code);
        Assert.Empty(slave.ReceivedRequests);
        Assert.Equal(0, session.Statistics.TxCount);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task SR2_Write_request_from_periodic_or_scan_priority_is_rejected()
    {
        var slave = new ModbusSlaveSimulator { UnitId = 1 };
        var (session, _) = CreateRtuSession(slave);
        await session.ConnectAsync();

        foreach (var priority in new[] { RequestPriority.Periodic, RequestPriority.Scan })
        {
            // 即使调用方自称“已确认”，扫描/周期队列也不允许发送写请求S规划 13.3 / CC-6）
            var request = new ModbusRequest(1, ModbusRequests.WriteSingleRegister(0, 1), priority, Confirmed: true);
            var result = await session.ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(ExchangeStatus.Rejected, result.Status);
            Assert.Equal(ErrorCodes.ValidationScanWriteBlocked, result.Error!.Code);
        }

        Assert.Empty(slave.ReceivedRequests);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task SR3_Broadcast_write_requires_dedicated_confirmation()
    {
        var slave = new ModbusSlaveSimulator { UnitId = 0 };
        var (session, _) = CreateRtuSession(slave);
        await session.ConnectAsync();

        // 广播S地址 0）+ 写：只给普通确认还不够，必须专项确认
        var notConfirmed = new ModbusRequest(0, ModbusRequests.WriteSingleRegister(0, 7), RequestPriority.Interactive,
            Confirmed: true, BroadcastConfirmed: false);
        var result = await session.ExecuteAsync(notConfirmed, CancellationToken.None);

        Assert.Equal(ExchangeStatus.Rejected, result.Status);
        Assert.Equal(ErrorCodes.ValidationBroadcastWrite, result.Error!.Code);
        Assert.Empty(slave.ReceivedRequests);

        // 专项确认后才允许发出S广播无响应）
        var confirmed = new ModbusRequest(0, ModbusRequests.WriteSingleRegister(0, 7), RequestPriority.Interactive,
            Confirmed: true, BroadcastConfirmed: true, NoResponseExpected: true);
        var sent = await session.ExecuteAsync(confirmed, CancellationToken.None);

        Assert.NotEqual(ExchangeStatus.Rejected, sent.Status);
        Assert.Single(slave.ReceivedRequests);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task SR2_Manual_send_refuses_write_frames_without_confirmation()
    {
        var slave = new ModbusSlaveSimulator { UnitId = 1 };
        var (session, _) = CreateRtuSession(slave);
        await session.ConnectAsync();

        var service = new ManualSendService(session);
        var codec = new ModbusRtuCodec();
        var frame = codec.BuildAdu(ModbusRequests.WriteSingleRegister(0, 9).ToBytes(), 1);

        var unconfirmed = await service.SendFrameAsync(frame, confirmedWrite: false, broadcastConfirmed: false,
            allowBadChecksum: false, expectResponse: true, timeoutMs: null, cancellationToken: CancellationToken.None);
        Assert.Equal(ExchangeStatus.Rejected, unconfirmed.Status);
        Assert.Empty(slave.ReceivedRequests);

        // 写操作必须被识别为“需要确认”S界面据此弹确认框）
        Assert.True(ManualSendService.RequiresWriteConfirmation(1, Fn.WriteSingleRegister, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));

        await session.DisposeAsync();
    }

    [Fact]
    public void SR2_Read_frames_do_not_require_write_confirmation()
    {
        Assert.False(ManualSendService.RequiresWriteConfirmation(1, Fn.ReadHoldingRegisters, out _));
        Assert.False(ManualSendService.RequiresWriteConfirmation(1, Fn.ReadInputRegisters, out _));
    }

    // =====================================================================================
    // SR-4：网卡配置不确认保留时可回滚；回滚后与快照一致
    // =====================================================================================

    [Fact]
    public void SR4_Adapter_apply_failure_rolls_back_to_the_snapshot()
    {
        var backend = new FakeAdapterBackend();
        var helper = new FakeNetConfigHelper();
        var directory = Path.Combine(AppContext.BaseDirectory, "security-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);

        try
        {
            var service = new AdapterConfigService(backend, helper, directory);

            // 校验通过 → 落快照 → 助手报“回读不一致SPLT-5005）且已回滚”
            helper.NextResponse = HelperResponse.Fail(ErrorCodes.PlatformNetConfigReadbackMismatch,
                "回读与写入不一致，已自动回滚");
            var result = service.ApplyStatic("adapter-1", new NetworkConfigRequest
            {
                Address = "192.168.44.10",
                PrefixLength = 24,
                ReplaceExistingAddresses = true
            });

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorCodes.PlatformNetConfigReadbackMismatch, result.ErrorCode);

            // 快照必须存在S“不确认保留时可回滚”的前提）
            var snapshots = service.ListSnapshots("adapter-1");
            Assert.Single(snapshots);
            Assert.False(snapshots[0].Kept);

            // 手工回滚：助手收到 restore，且回滚后网卡的地址与快照一致
            helper.NextResponse = HelperResponse.Ok(message: "已恢复");
            var restore = service.Restore(snapshots[0]);

            Assert.True(restore.IsSuccess, restore.ToUserText());
            Assert.Equal(HelperCommands.Restore, helper.Requests.Last().Command);
            Assert.Equal(new[] { "192.168.1.10" }, backend.Current.Ipv4.Select(a => a.Address).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SR4_Adapter_write_without_helper_is_blocked_and_leaves_no_snapshot()
    {
        var backend = new FakeAdapterBackend();
        var helper = new FakeNetConfigHelper { IsHelperPresent = false };
        var directory = Path.Combine(AppContext.BaseDirectory, "security-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);

        try
        {
            var service = new AdapterConfigService(backend, helper, directory);
            var result = service.ApplyStatic("adapter-1", new NetworkConfigRequest
            {
                Address = "192.168.44.10",
                PrefixLength = 24
            });

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorCodes.PlatformHelperUnavailable, result.ErrorCode);
            Assert.Empty(helper.Requests);              // 助手不可用时一条命令都不发
            Assert.Empty(service.ListSnapshots());      // 也不留下无用的快照
            Assert.Equal("192.168.1.10", backend.Current.Ipv4[0].Address);   // 网卡未被改动
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SR4_Invalid_input_is_rejected_before_any_snapshot_or_helper_call()
    {
        var backend = new FakeAdapterBackend();
        var helper = new FakeNetConfigHelper();
        var directory = Path.Combine(AppContext.BaseDirectory, "security-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);

        try
        {
            var service = new AdapterConfigService(backend, helper, directory);

            // 网络地址S192.168.1.0/24 的网络地址）必须被拒绝SVAL-4009）
            var result = service.ApplyStatic("adapter-1", new NetworkConfigRequest
            {
                Address = "192.168.1.0",
                PrefixLength = 24
            });

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorCodes.ValidationNetworkConfig, result.ErrorCode);
            Assert.Empty(helper.Requests);
            Assert.Empty(service.ListSnapshots());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // =====================================================================================
    // SR-5：日志默认关闭；关闭时不产生文件；开启提示可能含现场数据
    // =====================================================================================

    [Fact]
    public async Task SR5_Log_switch_off_creates_no_file_and_on_creates_one()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "security-tests", "logs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);

        try
        {
            var options = new LogOptions { Enabled = false, Directory = directory, MinimumLevel = LogLevel.Debug };
            var gate = new LogGate(options);

            gate.Info("SR5", "关闭状态下的日志S只应进入内存环形缓冲）");
            gate.Warning("SR5", "关闭状态下不落盘的警告");
            await Task.Delay(150);

            Assert.False(gate.Enabled);
            Assert.Null(gate.ActiveLogFile);
            Assert.Empty(Directory.GetFiles(directory));      // 关闭时不创建、不写入任何文件

            // 内存环形缓冲仍然可用S用于“复制诊断信息”与崩溃报告）
            Assert.Contains("关闭状态下的日志", gate.BuildDiagnosticText());

            // 打开后立即生效S无需重启），并创建 debug-*.log
            gate.SetEnabled(true);
            gate.Info("SR5", "打开后的日志");
            await Task.Delay(400);
            Assert.True(gate.Enabled);

            // 释放写入器后才能读取文件内容S文件句柄仍被日志写入器持有）
            await gate.DisposeAsync();

            var files = Directory.GetFiles(directory, "debug-*.log");
            Assert.Single(files);
            Assert.Contains("打开后的日志", await File.ReadAllTextAsync(files[0]));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // 句柄尚未释放时忽略清理失败
            }
        }
    }

    // =====================================================================================
    // SR-6：软件不会自动联网上传任何数据S对源码做静态断言）
    // =====================================================================================

    [Fact]
    public void SR6_Source_code_contains_no_outbound_upload_apis()
    {
        var root = RepoRoot();
        var forbidden = new[]
        {
            "HttpClient", "WebClient", "HttpWebRequest", "WebRequest.Create", "SmtpClient",
            "TelemetryClient", "ApplicationInsights", "GoogleAnalytics", "UploadString", "UploadFile",
            "RestClient", "SendGridClient", "FirebaseApp"
        };

        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            var relative = Path.GetRelativePath(root, file);
            // 更新服务是用户主动触发的联网组件，允许使用 HttpClientSSR-6 例外，见 内部设计文档）。
            if (relative.Equals(Path.Combine("src", "Comm.App.Wpf", "Services", "Update", "UpdateService.cs"), StringComparison.OrdinalIgnoreCase)) continue;

            var text = File.ReadAllText(file);
            foreach (var api in forbidden.Where(api => text.Contains(api, StringComparison.Ordinal)))
            {
                offenders.Add($"{relative} 出现 {api}");
            }
        }

        Assert.True(offenders.Count == 0,
            "发现可能自动联网/上传的 APIS规划 SR-6：本工具不得自动上传任何数据）：" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void SR6_Outbound_sockets_only_appear_in_the_expected_transport_and_probe_files()
    {
        var root = RepoRoot();

        // 允许发起出站连接的文件清单S传输通道 + 局域网探测 + 网卡冲突探测）。
        var allowedFiles = new[]
        {
            Path.Combine("src", "Comm.Transport", "TcpChannel.cs"),
            Path.Combine("src", "Comm.Network", "Discovery", "SystemHostProbe.cs"),
            Path.Combine("src", "Comm.Network", "Discovery", "LanScanner.cs"),
            Path.Combine("src", "Comm.Platform.Windows", "IpHelperBackend.cs"),
            Path.Combine("src", "Comm.Platform.Windows", "NetConfigHelperClient.cs"),
            Path.Combine("src", "Comm.Platform.Windows", "NetConfigSupport.cs"),
            Path.Combine("src", "Comm.Platform.Windows", "NetshBackend.cs"),
            Path.Combine("src", "Comm.NetConfigHelper", "HelperServer.cs"),
            Path.Combine("src", "Comm.NetConfigHelper", "Program.cs"),
            Path.Combine("src", "Comm.App.Wpf", "Services", "Update", "UpdateService.cs")
        };

        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            var relative = Path.GetRelativePath(root, file);
            var text = File.ReadAllText(file);

            bool usesOutbound = text.Contains("new TcpClient", StringComparison.Ordinal) ||
                                text.Contains("new Socket(", StringComparison.Ordinal) ||
                                text.Contains("Dns.GetHostAddresses", StringComparison.Ordinal) ||
                                text.Contains("Dns.GetHostEntry", StringComparison.Ordinal) ||
                                text.Contains("NamedPipeClientStream", StringComparison.Ordinal) ||
                                text.Contains("Ping(", StringComparison.Ordinal) ||
                                text.Contains("SendARP", StringComparison.Ordinal);

            if (!usesOutbound) continue;
            if (allowedFiles.Any(allowed => relative.EndsWith(allowed, StringComparison.OrdinalIgnoreCase))) continue;

            offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            "以下文件发起了出站连接但不在允许清单内S需要评审是否属于多余外联）：" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    // =====================================================================================
    // SR-7：插件只从受信目录加载；写能力必须声明并暴露给界面
    // =====================================================================================

    [Fact]
    public void SR7_Plugin_outside_the_trusted_directory_is_never_loaded()
    {
        // 受信目录里放一个合法插件；受信目录之外S父目录）再放一个 plugin.json，
        // 宿主必须只加载受信目录下的那一个。
        // 注意：本机受限环境下子进程不能写系统 TEMP，统一放在测试输出目录里。
        var root = Path.Combine(AppContext.BaseDirectory, "security-tests", "plugin-" + Guid.NewGuid().ToString("N")[..8]);
        var trusted = Path.Combine(root, "plugins");
        Directory.CreateDirectory(trusted);

        try
        {
            // 父目录里的诱饵清单S如果宿主错误地向上扫描就会被发现）
            File.WriteAllText(Path.Combine(root, "plugin.json"),
                """{"id":"decoy","name":"诱饵插件","version":"1.0.0","hostVersionRange":"*"}""");

            var host = new Comm.Plugins.Host.PluginHost(trusted, new Comm.Plugins.Host.InMemoryPluginStateStore(), new Version(0, 4, 0));
            host.LoadAll();

            Assert.DoesNotContain(host.Plugins, p => p.Id == "decoy");
            Assert.Equal(Path.GetFullPath(trusted), host.PluginDirectory);
            host.Dispose();
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception)
            {
                // 忽略清理失败
            }
        }
    }

    [Fact]
    public void SR7_Manifest_write_capability_is_surfaced_for_ui_confirmation()
    {
        var manifest = new Comm.Plugins.Abstractions.PluginManifest
        {
            Id = "writer",
            Name = "会写设备的插件",
            Version = "1.0.0",
            HostVersionRange = "*",
            MayWrite = true,
            RequiredPrivilege = "网卡配置"
        };

        // 清单声明写能力 → 界面必须能拿到风险文本S用于额外提示，规划 SR-7 / 15.11）
        var info = new Comm.Plugins.Host.PluginInfo(
            manifest.Id, manifest.Name, manifest.Version, manifest.Author, manifest.Description,
            "/plugins/writer", Comm.Plugins.Host.PluginState.Loaded, null, manifest.Capabilities,
            manifest.MayWrite, manifest.RequiredPrivilege, 0, false);

        Assert.True(info.MayWrite);
        Assert.Contains("写", info.RiskText);
        Assert.Equal("网卡配置", info.RequiredPrivilege);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CommTestTool.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根目录");
    }

    // =====================================================================================
    // 测试用的假后端/假助手S与 Comm.Platform.Windows.Tests 的同类替身保持相同契约）
    // =====================================================================================

    private sealed class FakeAdapterBackend : INetworkAdapterBackend
    {
        private AdapterSnapshot _current = new()
        {
            Id = "adapter-1",
            Name = "测试网卡",
            Description = "Fake Adapter",
            MacAddress = "00-11-22-33-44-55",
            IsUp = true,
            DhcpEnabled = false,
            Ipv4 = new[] { new Ipv4AddressInfo("192.168.1.10", 24, "255.255.255.0") },
            Gateways = new[] { "192.168.1.1" },
            DnsServers = Array.Empty<string>()
        };

        public AdapterSnapshot Current => _current;

        public IReadOnlyList<AdapterSnapshot> ReadAdapters() => new[] { _current };

        public AdapterSnapshot? ReadAdapter(string adapterId) => adapterId == _current.Id ? _current : null;

        public NetConfigResult Apply(string adapterId, NetworkConfigRequest request)
        {
            _current = _current with
            {
                DhcpEnabled = request.DhcpEnabled,
                Ipv4 = request.DhcpEnabled
                    ? Array.Empty<Ipv4AddressInfo>()
                    : new[] { new Ipv4AddressInfo(request.Address ?? string.Empty, request.PrefixLength, "255.255.255.0") }
            };
            return NetConfigResult.Ok(_current);
        }

        public NetConfigResult Restore(string adapterId, AdapterSnapshot snapshot)
        {
            _current = snapshot;
            return NetConfigResult.Ok(_current);
        }

        public NetConfigResult AddAuxiliaryAddress(string adapterId, string address, int prefixLength)
        {
            _current = _current with
            {
                Ipv4 = _current.Ipv4.Append(new Ipv4AddressInfo(address, prefixLength, "255.255.255.0")).ToArray()
            };
            return NetConfigResult.Ok(_current);
        }

        public NetConfigResult RemoveAuxiliaryAddress(string adapterId, string address)
        {
            _current = _current with { Ipv4 = _current.Ipv4.Where(a => a.Address != address).ToArray() };
            return NetConfigResult.Ok(_current);
        }
    }

    private sealed class FakeNetConfigHelper : INetConfigHelper
    {
        public bool IsHelperPresent { get; set; } = true;

        public string ExpectedPath => "/fake/helper.exe";

        public string StatusText => IsHelperPresent ? "助手已就绪S假实现）" : "未找到助手";

        public HelperResponse NextResponse { get; set; } = HelperResponse.Ok("已应用");

        public List<HelperRequest> Requests { get; } = new();

        public HelperResponse Send(HelperRequest request, int timeoutMs = 60000)
        {
            Requests.Add(request);
            return IsHelperPresent
                ? NextResponse
                : HelperResponse.Fail(ErrorCodes.PlatformHelperUnavailable, "未找到提权助手");
        }

        public void Dispose()
        {
        }
    }
}
