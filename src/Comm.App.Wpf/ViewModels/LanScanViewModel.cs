using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using Comm.App.Wpf.Services;
using Comm.Core.Tasks;
using Comm.Network;
using Comm.Network.Discovery;
using Comm.Scanning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comm.App.Wpf.ViewModels;

/// <summary>
/// 局域网设备扫描（M2-2，规划 4.7，需求 R-06）。
/// 结果分级：已发现 / 端口开放 / Modbus 有响应 / 未确认；**ICMP 无响应不等于离线**。
/// </summary>
public partial class LanScanViewModel : ObservableObject
{
    private readonly AppServices _app;
    private CancellationTokenSource? _cts;
    private PauseGate? _gate;

    public LanScanViewModel(AppServices app)
    {
        _app = app;
        var scan = app.Settings.Scan;
        _maxAddresses = scan.MaxLanScanAddresses;
        _concurrency = scan.LanScanConcurrency;
        _perHostTimeoutMs = scan.LanScanPerHostTimeoutMs;

        var local = NetworkAdapters.Read().FirstOrDefault(a => a.PrimaryIpv4 != "-" && !a.IsLoopback);
        if (local is not null)
        {
            _ipAddress = local.PrimaryIpv4;
            _maskText = local.PrimaryMask == "-" ? "255.255.255.0" : local.PrimaryMask;
        }

        ResultsView = CollectionViewSource.GetDefaultView(Results);
        ResultsView.Filter = o => !OnlyResponsive || ((LanHostResult)o).State != LanHostState.Unconfirmed;
        UpdateHints();
    }

    public ObservableCollection<LanHostResult> Results { get; } = new();
    public ICollectionView ResultsView { get; }

    [ObservableProperty] private string _ipAddress = "192.168.1.10";
    [ObservableProperty] private string _maskText = "255.255.255.0";
    [ObservableProperty] private string _rangeText = string.Empty;
    [ObservableProperty] private string _portText = "502";
    [ObservableProperty] private bool _useArp = true;
    [ObservableProperty] private bool _useIcmp = true;
    [ObservableProperty] private bool _identifyModbus;
    [ObservableProperty] private bool _resolveHostName;
    [ObservableProperty] private int _maxAddresses = 4096;
    [ObservableProperty] private int _concurrency = 64;
    [ObservableProperty] private int _perHostTimeoutMs = 800;
    [ObservableProperty] private int _sendDelayMs;
    [ObservableProperty] private byte _unitId = 1;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _onlyResponsive = true;
    [ObservableProperty] private string _progressText = "未开始";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _rangeHint = string.Empty;
    [ObservableProperty] private LanHostResult? _selectedHost;

    partial void OnIpAddressChanged(string value) => UpdateHints();
    partial void OnMaskTextChanged(string value) => UpdateHints();
    partial void OnRangeTextChanged(string value) => UpdateHints();
    partial void OnMaxAddressesChanged(int value) => UpdateHints();
    partial void OnPerHostTimeoutMsChanged(int value) => UpdateHints();
    partial void OnConcurrencyChanged(int value) => UpdateHints();
    partial void OnOnlyResponsiveChanged(bool value) => ResultsView.Refresh();

    private LanScanOptions BuildOptions()
    {
        var ports = new List<int>();
        foreach (var part in PortText.Split(new[] { ',', '，', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, out var port) && port is > 0 and <= 65535) ports.Add(port);
        }
        if (ports.Count == 0) ports.Add(502);

        return new LanScanOptions
        {
            IpAddress = string.IsNullOrWhiteSpace(IpAddress) ? null : IpAddress.Trim(),
            MaskText = string.IsNullOrWhiteSpace(MaskText) ? null : MaskText.Trim(),
            CidrOrRange = string.IsNullOrWhiteSpace(RangeText) ? null : RangeText.Trim(),
            TcpPorts = ports,
            UseArp = UseArp,
            UseIcmp = UseIcmp,
            IdentifyModbus = IdentifyModbus,
            ResolveHostName = ResolveHostName,
            MaxAddresses = Math.Max(1, MaxAddresses),
            Concurrency = Math.Clamp(Concurrency, 16, 256),
            PerHostTimeoutMs = Math.Clamp(PerHostTimeoutMs, 20, 30_000),
            SendDelayMs = Math.Max(0, SendDelayMs),
            UnitId = UnitId
        };
    }

    /// <summary>范围提示：先算一遍地址数量与预计耗时，超限时明确拦截（规划 4.7.1）。</summary>
    private void UpdateHints()
    {
        var options = BuildOptions();
        var range = LanScanRange.Build(options);

        if (!range.IsSuccess)
        {
            RangeHint = $"⚠ {range.Error!.Message}" +
                        (string.IsNullOrEmpty(range.Error.Hint) ? string.Empty : $"\n建议：{range.Error.Hint}");
            return;
        }

        var hosts = range.Value!;
        double seconds = hosts.Count * (double)options.EffectiveTimeoutMs() / options.EffectiveConcurrency() / 1000.0;
        RangeHint = $"地址数量 {hosts.Count} 个（{hosts[0]} ~ {hosts[^1]}）；并发 {options.EffectiveConcurrency()}，" +
                    $"单主机超时 {options.EffectiveTimeoutMs()} ms；最坏情况预计约 " +
                    (seconds < 60 ? $"{seconds:F1} 秒" : $"{seconds / 60:F1} 分钟") +
                    "\n说明：ARP 只在同一二层网段有效，路由之后拿不到 MAC；ICMP 无响应不等于设备离线。";
    }

    [RelayCommand]
    private void UseLocalAdapter()
    {
        var local = NetworkAdapters.Read().FirstOrDefault(a => a.PrimaryIpv4 != "-" && !a.IsLoopback);
        if (local is null)
        {
            _app.Dialogs.Warn("没有可用的本机地址", "未找到带 IPv4 地址的网络接口。");
            return;
        }

        IpAddress = local.PrimaryIpv4;
        MaskText = local.PrimaryMask == "-" ? MaskText : local.PrimaryMask;
        RangeText = string.Empty;
        UpdateHints();
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        var options = BuildOptions();
        var range = LanScanRange.Build(options);
        if (!range.IsSuccess)
        {
            _app.Dialogs.ShowError(range.Error!, "扫描范围不正确");
            return;
        }

        var hosts = range.Value!;
        if (hosts.Count > 1024 && !_app.Dialogs.Confirm("确认扫描范围",
                $"即将扫描 {hosts.Count} 个地址（{hosts[0]} ~ {hosts[^1]}），并发 {options.EffectiveConcurrency()}。\n\n" +
                "请确认已获得该网段的扫描授权，且不会影响生产网络。是否继续？"))
        {
            return;
        }

        var scanner = new LanScanner(new SystemHostProbe(),
            options.IdentifyModbus ? _app.Sessions.CreateLanIdentifyDelegate() : null);

        Results.Clear();
        _cts = new CancellationTokenSource();
        _gate = new PauseGate();
        IsRunning = true;
        IsPaused = false;
        ProgressPercent = 0;
        ProgressText = "开始扫描…";
        StatusMessage = $"正在扫描 {hosts.Count} 个地址：{hosts[0]} ~ {hosts[^1]}";

        _app.Update(s => s with
        {
            Scan = s.Scan with
            {
                MaxLanScanAddresses = options.MaxAddresses,
                LanScanConcurrency = options.Concurrency,
                LanScanPerHostTimeoutMs = options.PerHostTimeoutMs
            }
        });

        _app.Log.Info("LanScan",
            $"开始局域网扫描：{hosts.Count} 个地址（{hosts[0]}~{hosts[^1]}），并发 {options.EffectiveConcurrency()}，" +
            $"ARP={options.UseArp}，ICMP={options.UseIcmp}，Modbus 识别={options.IdentifyModbus}");

        var progress = new Progress<LanScanProgress>(p =>
        {
            ProgressText = p.ToText();
            ProgressPercent = p.Percent;
            _app.Tasks.Report(new ScanProgress("局域网扫描", p.Completed, p.Total, p.CurrentStep,
                ScanStatus.Running));
        });

        var hostProgress = new Progress<LanHostResult>(host => Results.Add(host));

        try
        {
            var results = await scanner.ScanAsync(options, _gate, progress, hostProgress, _cts.Token)
                .ConfigureAwait(true);

            int discovered = results.Count(r => r.State == LanHostState.Discovered);
            int ports = results.Count(r => r.State == LanHostState.PortOpen);
            int modbus = results.Count(r => r.State == LanHostState.ModbusResponding);
            StatusMessage = $"扫描完成：已发现 {discovered} 个，端口开放 {ports} 个，Modbus 有响应 {modbus} 个，" +
                            $"未确认 {results.Count - discovered - ports - modbus} 个。";

            if (results.Any(r => r.State == LanHostState.Unconfirmed))
            {
                StatusMessage += " 未确认的主机可能被防火墙拦截或在路由之后，不能判定为离线。";
            }

            _app.Log.Info("LanScan", StatusMessage);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"扫描已停止，已获得 {Results.Count} 条结果。";
            _app.Log.Info("LanScan", "局域网扫描被用户停止");
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描异常终止：{ex.Message}";
            _app.Log.Error("LanScan", "局域网扫描异常终止", ex);
            _app.Dialogs.Error("扫描失败", ex.Message);
        }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            _cts?.Dispose();
            _cts = null;
            _gate = null;
            _app.Tasks.Idle();
        }
    }

    [RelayCommand]
    private void PauseResume()
    {
        if (_gate is null) return;
        if (_gate.IsPaused)
        {
            _gate.Resume();
            IsPaused = false;
        }
        else
        {
            _gate.Pause();
            IsPaused = true;
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void Clear()
    {
        Results.Clear();
        StatusMessage = string.Empty;
        ProgressText = "未开始";
        ProgressPercent = 0;
    }

    [RelayCommand]
    private void Export()
    {
        if (Results.Count == 0)
        {
            _app.Dialogs.Warn("无数据", "还没有扫描结果可以导出。");
            return;
        }

        var path = _app.Dialogs.SaveFile("导出局域网扫描结果", "CSV 文件 (*.csv)|*.csv",
            $"局域网扫描-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (string.IsNullOrEmpty(path)) return;

        var error = LanScanner.ExportCsv(path, Results.ToList(),
            $"{IpAddress}/{MaskText}，端口 {PortText}，并发 {Concurrency}");
        if (string.IsNullOrEmpty(error)) _app.Dialogs.Info("导出完成", $"已导出 {Results.Count} 条结果到：\n{path}");
        else _app.Dialogs.Error("导出失败", error);
    }

    /// <summary>把发现的 IP 回填到“连接中心”并收窄范围为该地址。</summary>
    [RelayCommand]
    private void UseAsTcpTarget()
    {
        if (SelectedHost is null) return;

        var port = BuildOptions().TcpPorts[0];
        _app.Update(s => s with { Tcp = s.Tcp with { Host = SelectedHost.Ip, Port = port } });
        _app.Notify($"已把 {SelectedHost.Ip}:{port} 设为 TCP 连接目标，请在“连接中心”点击“连接 TCP”。");
        StatusMessage = $"已把 {SelectedHost.Ip}:{port} 设为 TCP 连接目标（到“连接中心”连接）。";
    }

    [RelayCommand]
    private void ShowDetail()
    {
        if (SelectedHost is null) return;
        var host = SelectedHost;

        var text = $"IP：{host.Ip}\n" +
                   $"MAC：{host.Mac ?? "-"}\n" +
                   $"主机名：{host.HostName ?? "-"}\n" +
                   $"发现方式：{host.KindText}\n" +
                   $"状态分级：{host.StateText}\n" +
                   $"端口状态：{host.PortText}\n" +
                   $"Modbus：{host.ModbusText}\n" +
                   $"耗时：{host.LatencyMs:F0} ms\n" +
                   $"首次发现：{host.FirstSeen:yyyy-MM-dd HH:mm:ss.fff}\n" +
                   $"最近响应：{host.LastSeen:yyyy-MM-dd HH:mm:ss.fff}\n" +
                   $"备注：{host.Note}\n\n" +
                   "提示：ARP 只在同一二层网段有效；未确认的主机可能被防火墙拦截或在路由之后。";
        _app.Dialogs.ShowText("主机详情", text);
    }
}
