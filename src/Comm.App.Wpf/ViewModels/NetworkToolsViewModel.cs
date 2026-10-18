using System.Collections.ObjectModel;
using System.Windows.Threading;
using Comm.App.Wpf.Services;
using Comm.Network;
using Comm.Platform.Windows;
using Comm.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comm.App.Wpf.ViewModels;

/// <summary>
/// 网络工具（规划 4.9 子网计算 + 4.8 本机网卡配置）：
/// <list type="bullet">
///   <item>IPv4 子网计算（纯计算，M1 已交付）；</item>
///   <item>本机网卡读取（只读，普通权限）；</item>
///   <item>静态 IPv4 / 掩码 / 网关 / DNS 配置、DHCP 切换、辅助 IP（R-07、R-13）——
///   **所有写操作都交给提权助手 <c>Comm.NetConfigHelper</c>**（AD-08），主程序始终保持普通权限。</item>
/// </list>
/// </summary>
public partial class NetworkToolsViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _app;
    private readonly AdapterConfigService _config;
    private readonly INetConfigHelper _helper;
    private readonly DispatcherTimer _countdownTimer;

    public NetworkToolsViewModel(AppServices app)
    {
        _app = app;

        // 只读后端（无需管理员）+ 提权助手客户端（写入的唯一通道，见规划 12.5）。
        var backend = AdapterBackendFactory.Create();
        _helper = new NetConfigHelperClient(log: LogNetConfig);
        _config = new AdapterConfigService(backend, _helper, app.DataDirectory, null, LogNetConfig);

        var netConfig = app.Settings.NetConfig;
        _autoRollbackEnabled = netConfig.SafetyRollbackEnabled;
        _autoRollbackSeconds = netConfig.SafetyRollbackSeconds;

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) => RefreshCountdown();

        Compute();
        RefreshAdapters();
        RefreshSnapshots();
        RefreshProfiles();
        RefreshHelperStatus();
        WarnAboutPendingSnapshot();
    }

    private void LogNetConfig(string category, string message, string? correlation) =>
        _app.Log.Info($"NetConfig/{category}", message, correlation);

    // =====================================================================================
    // 一、IPv4 子网计算（规划 4.9，M1 已交付，行为保持不变）
    // =====================================================================================

    [ObservableProperty] private string _ipText = "192.168.1.10";
    [ObservableProperty] private string _maskText = "255.255.255.0";
    [ObservableProperty] private string _secondIpText = string.Empty;

    [ObservableProperty] private string _networkText = "-";
    [ObservableProperty] private string _broadcastText = "-";
    [ObservableProperty] private string _hostRangeText = "-";
    [ObservableProperty] private string _cidrText = "-";
    [ObservableProperty] private string _wildcardText = "-";
    [ObservableProperty] private string _totalText = "-";
    [ObservableProperty] private string _kindText = "-";
    [ObservableProperty] private string _binaryText = "-";
    [ObservableProperty] private string _maskBinaryText = "-";
    [ObservableProperty] private string _sameSubnetText = "（未填写第二个 IP）";
    [ObservableProperty] private string _estimatedScanText = "-";
    [ObservableProperty] private string _hostListText = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private LocalAdapterInfo? _selectedAdapter;

    [RelayCommand]
    private void Compute()
    {
        var result = SubnetCalculator.Compute(IpText, MaskText);
        if (!result.IsSuccess)
        {
            StatusMessage = result.Error!.Message;
            NetworkText = BroadcastText = HostRangeText = CidrText = "-";
            return;
        }

        var info = result.Value!;
        NetworkText = info.NetworkText;
        BroadcastText = info.BroadcastText;
        HostRangeText = info.HostRangeText;
        CidrText = info.Cidr;
        WildcardText = info.WildcardText;
        TotalText = $"{info.TotalAddresses} 个地址，可用主机 {info.UsableHosts} 个" +
                    (info.IsSingleHost ? "（/32 单主机）" : info.IsPointToPoint ? "（/31 点对点，两端均可用）" : string.Empty);
        KindText = info.AddressKind;
        BinaryText = info.AddressBinary;
        MaskBinaryText = info.MaskBinary;
        EstimatedScanText = SubnetCalculator.EstimatedScanTimeText(info);

        if (!string.IsNullOrWhiteSpace(SecondIpText))
        {
            var same = SubnetCalculator.CheckSameSubnet(IpText, SecondIpText, MaskText);
            SameSubnetText = same.IsSuccess
                ? (same.Value ? "两个 IP 属于同一网段（可直接通信）" : "两个 IP 不属于同一网段（需要网关或修改掩码）")
                : same.Error!.Message;
        }
        else
        {
            SameSubnetText = "（未填写第二个 IP）";
        }

        var hosts = SubnetCalculator.EnumerateHosts(info, _app.Settings.Scan.MaxLanScanAddresses);
        HostListText = hosts is null
            ? $"地址数量超过 {_app.Settings.Scan.MaxLanScanAddresses}，未展开列表（仅显示范围，避免界面卡顿）"
            : string.Join(Environment.NewLine, hosts);

        StatusMessage = $"计算完成：{info.Cidr}，网络地址 {info.NetworkText}，广播地址 {info.BroadcastText}，可用主机 {info.UsableHosts} 个。";
        _app.Log.Debug("Network", StatusMessage);
    }

    // =====================================================================================
    // 二、本机网络接口（只读列表 + 子网计算联动）
    // =====================================================================================

    public ObservableCollection<LocalAdapterInfo> Adapters { get; } = new();

    [RelayCommand]
    private void RefreshAdapters()
    {
        Adapters.Clear();
        foreach (var adapter in NetworkAdapters.Read()) Adapters.Add(adapter);

        RefreshConfiguredAdapters();
        StatusMessage = $"已读取 {Adapters.Count} 个本机网络接口；网卡修改由提权助手执行（主程序不需要管理员权限）。";
    }

    [RelayCommand]
    private void UseAdapterAddress()
    {
        if (SelectedAdapter is null) return;
        if (SelectedAdapter.PrimaryIpv4 == "-")
        {
            _app.Dialogs.Warn("无可用的 IPv4", "该接口当前没有 IPv4 地址。");
            return;
        }

        IpText = SelectedAdapter.PrimaryIpv4;
        MaskText = SelectedAdapter.PrimaryMask == "-" ? MaskText : SelectedAdapter.PrimaryMask;
        Compute();
    }

    [RelayCommand]
    private void UseLocalRange()
    {
        var local = NetworkAdapters.Read().FirstOrDefault(a => a.PrimaryIpv4 != "-");
        if (local is null)
        {
            _app.Dialogs.Warn("没有可用的本机地址", "未找到带 IPv4 地址的网络接口。");
            return;
        }

        IpText = local.PrimaryIpv4;
        MaskText = local.PrimaryMask;
        Compute();
    }

    [RelayCommand]
    private void CopyCidr() => Copy(CidrText);

    [RelayCommand]
    private void CopyHostRange() => Copy(HostRangeText);

    [RelayCommand]
    private void CopyHostList() => Copy(HostListText);

    [RelayCommand]
    private void CopyDetails()
    {
        var text = $"IP：{IpText}\n子网掩码：{MaskText}\nCIDR：{CidrText}\n网络地址：{NetworkText}\n广播地址：{BroadcastText}\n" +
                   $"可用主机范围：{HostRangeText}\n地址数量：{TotalText}\n通配符掩码：{WildcardText}\n地址类型：{KindText}\n" +
                   $"二进制：{BinaryText}\n掩码二进制：{MaskBinaryText}\n同网段判断：{SameSubnetText}";
        Copy(text);
    }

    private void Copy(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            StatusMessage = "已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            _app.Dialogs.Error("复制失败", ex.Message);
        }
    }

    // =====================================================================================
    // 三、网卡配置（R-07 / R-13：写操作一律经提权助手，见规划 4.8、12.5、15.8）
    // =====================================================================================

    /// <summary>可配置网卡列表（来自提权助手后端的只读枚举）。</summary>
    public ObservableCollection<AdapterSnapshot> ConfigAdapters { get; } = new();

    /// <summary>当前可选的辅助/已有地址（用于“移除辅助 IP”）。</summary>
    public ObservableCollection<Ipv4AddressInfo> SelectedAddresses { get; } = new();

    /// <summary>配置快照（修改前自动落盘，可一键恢复，规划 15.8）。</summary>
    public ObservableCollection<AdapterSnapshotFile> Snapshots { get; } = new();

    /// <summary>保存的网卡配置方案（规划 4.8.4）。</summary>
    public ObservableCollection<NetworkProfile> Profiles { get; } = new();

    [ObservableProperty] private AdapterSnapshot? _selectedConfigAdapter;
    [ObservableProperty] private Ipv4AddressInfo? _selectedAddress;
    [ObservableProperty] private AdapterSnapshotFile? _selectedSnapshot;
    [ObservableProperty] private NetworkProfile? _selectedProfile;

    /// <summary>选中网卡的当前配置摘要（只读显示，来源是提权助手回读的快照）。</summary>
    [ObservableProperty] private string _adapterSummaryText = "未选择网卡。";

    /// <summary>编辑模式：true = DHCP，false = 静态。</summary>
    [ObservableProperty] private bool _useDhcp;

    [ObservableProperty] private string _configIpv4Text = string.Empty;
    [ObservableProperty] private string _configPrefixText = "24";
    [ObservableProperty] private string _configGatewayText = string.Empty;
    [ObservableProperty] private string _configDns1Text = string.Empty;
    [ObservableProperty] private string _configDns2Text = string.Empty;
    [ObservableProperty] private bool _applyDns;

    /// <summary>勾选 = 替换网卡上现有地址；不勾 = 保留原有地址（等价于辅助 IP 语义，规划 4.8.2）。</summary>
    [ObservableProperty] private bool _replaceExistingAddresses = true;

    [ObservableProperty] private string _validationText = "尚未校验。点击“校验并预览”检查输入。";
    [ObservableProperty] private bool _canApply;

    [ObservableProperty] private bool _autoRollbackEnabled;
    [ObservableProperty] private int _autoRollbackSeconds = 60;
    [ObservableProperty] private string _rollbackText = "未启用安全恢复倒计时。";
    [ObservableProperty] private bool _isRollbackArmed;

    [ObservableProperty] private string _auxAddressText = string.Empty;
    [ObservableProperty] private string _auxPrefixText = "24";

    [ObservableProperty] private string _profileNameText = string.Empty;

    [ObservableProperty] private string _helperStatusText = string.Empty;
    [ObservableProperty] private string _helperPathText = string.Empty;

    partial void OnSelectedConfigAdapterChanged(AdapterSnapshot? value)
    {
        SelectedAddresses.Clear();
        if (value is not null)
        {
            foreach (var address in value.Ipv4) SelectedAddresses.Add(address);
            AdapterSummaryText = $"{value.Name}（{value.Description}）\n{value.SummaryText}\nMAC {value.MacAddress}" +
                                 (value.IsVirtual ? " · 虚拟/隧道类网卡" : string.Empty);

            // 用当前值预填编辑区（用户仍可修改；应用前必须再次校验）。
            if (value.Ipv4.FirstOrDefault() is { } primary)
            {
                ConfigIpv4Text = primary.Address;
                ConfigPrefixText = primary.PrefixLength.ToString();
            }

            ConfigGatewayText = value.Gateways.FirstOrDefault() ?? string.Empty;
            ConfigDns1Text = value.DnsServers.ElementAtOrDefault(0) ?? string.Empty;
            ConfigDns2Text = value.DnsServers.ElementAtOrDefault(1) ?? string.Empty;
            UseDhcp = value.DhcpEnabled;
        }
        else
        {
            AdapterSummaryText = "未选择网卡。";
        }

        ValidationText = "尚未校验。点击“校验并预览”检查输入。";
        CanApply = false;
        RefreshSnapshots();
    }

    /// <summary>只读刷新网卡列表与配置快照（不触发任何写操作）。</summary>
    [RelayCommand]
    private void RefreshConfiguredAdapters()
    {
        var previous = SelectedConfigAdapter?.Id;
        ConfigAdapters.Clear();
        foreach (var adapter in _config.ReadAdapters()) ConfigAdapters.Add(adapter);

        SelectedConfigAdapter = ConfigAdapters.FirstOrDefault(a => a.Id == previous)
                                ?? ConfigAdapters.FirstOrDefault(a => a.IsUp && !a.IsLoopback)
                                ?? ConfigAdapters.FirstOrDefault();

        RefreshSnapshots();
    }

    [RelayCommand]
    private void RefreshHelperStatus()
    {
        HelperStatusText = _helper.StatusText;
        HelperPathText = $"预期路径：{_helper.ExpectedPath}\n搜索顺序：{NetConfigHelperLocator.DescribeSearchPaths()}";
    }

    private NetworkConfigRequest BuildRequest()
    {
        var prefix = NetworkConfigValidator.ParsePrefixLength(ConfigPrefixText);
        return new NetworkConfigRequest
        {
            DhcpEnabled = UseDhcp,
            Address = ConfigIpv4Text.Trim(),
            PrefixLength = prefix.IsSuccess ? prefix.Value : -1,
            ReplaceExistingAddresses = ReplaceExistingAddresses,
            Gateway = ConfigGatewayText.Trim(),
            ClearGateway = string.IsNullOrWhiteSpace(ConfigGatewayText),
            ApplyDns = ApplyDns,
            Dns1 = ConfigDns1Text.Trim(),
            Dns2 = ConfigDns2Text.Trim(),
            AutoRollbackSeconds = AutoRollbackEnabled ? Math.Clamp(AutoRollbackSeconds, 15, 3600) : 0
        };
    }

    /// <summary>校验并预览：显示规范化后的网络地址/广播地址/主机范围与风险提示（规划 15.8 第 2 步）。</summary>
    [RelayCommand]
    private void ValidateConfig()
    {
        if (SelectedConfigAdapter is null)
        {
            _app.Dialogs.Warn("请先选择网卡", "请在上方列表中选中要配置的本机网卡。");
            return;
        }

        _app.Log.Info("NetConfig", $"校验网卡配置：{SelectedConfigAdapter.Name}，模式={(UseDhcp ? "DHCP" : "静态")}");

        var result = _config.Validate(SelectedConfigAdapter.Id, BuildRequest());
        if (!result.IsSuccess)
        {
            CanApply = false;
            ValidationText = $"❌ 校验未通过\n{result.Error!.Code}：{result.Error.Message}" +
                             (string.IsNullOrWhiteSpace(result.Error.Hint) ? string.Empty : $"\n建议：{result.Error.Hint}");
            return;
        }

        var validation = result.Value!;
        CanApply = !validation.IsNoOp;
        ValidationText = (validation.IsNoOp ? "⚠ 无需修改\n" : "✔ 校验通过\n") + validation.SummaryText +
                         (string.IsNullOrWhiteSpace(validation.Warning) ? string.Empty : $"\n提示：{validation.Warning}");
    }

    /// <summary>应用配置（静态地址 / DHCP）。写操作由提权助手执行，应用前必须二次确认（规划 4.8.3）。</summary>
    [RelayCommand]
    private void ApplyConfig()
    {
        if (SelectedConfigAdapter is null)
        {
            _app.Dialogs.Warn("请先选择网卡", "请在上方列表中选中要配置的本机网卡。");
            return;
        }

        var adapter = SelectedConfigAdapter;
        var request = BuildRequest();

        // 复用服务的校验结果（服务内部会再校验一次，且失败时不落快照、不下发）。
        var validation = _config.Validate(adapter.Id, request);
        if (!validation.IsSuccess)
        {
            CanApply = false;
            ValidationText = $"❌ 校验未通过\n{validation.Error!.Code}：{validation.Error.Message}";
            _app.Dialogs.Warn("配置不合法", validation.Error.ToUserText());
            return;
        }

        if (validation.Value!.IsNoOp)
        {
            _app.Dialogs.Info("无需修改", "目标配置与网卡当前配置一致，没有需要应用的改动。");
            return;
        }

        var modeText = UseDhcp
            ? "DHCP（自动获取地址）"
            : $"{validation.Value.Address}/{validation.Value.PrefixLength}（掩码 {validation.Value.Mask}）" +
              (string.IsNullOrWhiteSpace(validation.Value.Gateway) ? "，不修改网关" : $"，网关 {validation.Value.Gateway}") +
              (validation.Value.ApplyDns ? $"，DNS {validation.Value.Dns1}" : "，不改 DNS") +
              (validation.Value.ReplaceExistingAddresses ? "，替换现有地址" : "，保留现有地址（叠加）");

        var riskText =
            "⚠ 修改本机网卡会立即影响所有网络连接；如果你正通过网络/远程桌面访问本机，连接可能中断。\n" +
            "⚠ 写操作由独立的提权助手（Comm.NetConfigHelper）执行，将弹出 UAC 提权窗口；主程序本身不需要管理员权限。\n" +
            (AutoRollbackEnabled
                ? $"✔ 已启用安全恢复倒计时：{Math.Clamp(AutoRollbackSeconds, 15, 3600)} 秒内未点击“保留此配置”将自动恢复。\n"
                : "提示：未启用安全恢复倒计时，如远程连接中断需在现场手工恢复。\n") +
            (string.IsNullOrWhiteSpace(validation.Value.Warning) ? string.Empty : $"提示：{validation.Value.Warning}\n") +
            "\n确认按以下配置修改该网卡吗？";

        var message = $"网卡：{adapter.Name}（{adapter.Description}）\n将要写入：{modeText}\n\n{riskText}";
        if (!_app.Dialogs.Confirm("确认修改本机网卡配置", message)) return;

        var result = UseDhcp
            ? _config.SetDhcp(adapter.Id, request.AutoRollbackSeconds)
            : _config.ApplyStatic(adapter.Id, request);

        ReportResult(result, "应用配置");
        RefreshConfiguredAdapters();
        RefreshSnapshots();
        RefreshCountdown();
    }

    [RelayCommand]
    private void AddAuxiliaryAddress()
    {
        if (SelectedConfigAdapter is null)
        {
            _app.Dialogs.Warn("请先选择网卡", "请先选中要叠加地址的本机网卡。");
            return;
        }

        var prefix = NetworkConfigValidator.ParsePrefixLength(AuxPrefixText);
        if (!prefix.IsSuccess)
        {
            _app.Dialogs.Warn("前缀不合法", prefix.Error!.ToUserText());
            return;
        }

        var check = NetworkConfigValidator.ValidateAuxiliaryAddress(AuxAddressText.Trim(), prefix.Value, SelectedConfigAdapter);
        if (!check.IsSuccess)
        {
            _app.Dialogs.Warn("地址不合法", check.Error!.ToUserText());
            return;
        }

        var value = check.Value!;
        if (value.IsNoOp && !_app.Dialogs.Confirm("地址已存在", $"{value.Warning}\n\n仍要重新应用一次吗？")) return;

        if (!_app.Dialogs.Confirm("确认叠加辅助 IP",
                $"网卡：{SelectedConfigAdapter.Name}\n将叠加地址：{value.Address}/{value.PrefixLength}\n\n" +
                "辅助 IP 会保留网卡原有地址（比整体替换更安全），且可一键移除。\n" +
                "⚠ 写操作由提权助手执行，将弹出 UAC 提权窗口。\n\n确认继续吗？")) return;

        var result = _config.AddAuxiliaryAddress(SelectedConfigAdapter.Id, value.Address, value.PrefixLength);
        ReportResult(result, "叠加辅助 IP");
        RefreshConfiguredAdapters();
        RefreshSnapshots();
    }

    [RelayCommand]
    private void RemoveAuxiliaryAddress()
    {
        if (SelectedConfigAdapter is null || SelectedAddress is null)
        {
            _app.Dialogs.Warn("请先选择地址", "请先在“网卡现有地址”列表中选中要移除的地址。");
            return;
        }

        var address = SelectedAddress;
        if (!_app.Dialogs.Confirm("确认移除地址",
                $"将从网卡「{SelectedConfigAdapter.Name}」移除地址 {address.Cidr}。\n\n" +
                "✔ 只移除该地址，不影响网卡上的其他地址。\n" +
                (address.Address == SelectedConfigAdapter.Ipv4.FirstOrDefault()?.Address
                    ? "⚠ 注意：这是网卡的主地址，移除后该网卡可能暂时无法通信。\n"
                    : string.Empty) +
                "⚠ 写操作由提权助手执行，将弹出 UAC 提权窗口。\n\n确认移除吗？")) return;

        var result = _config.RemoveAuxiliaryAddress(SelectedConfigAdapter.Id, address.Address);
        ReportResult(result, "移除地址");
        RefreshConfiguredAdapters();
        RefreshSnapshots();
    }

    /// <summary>确认保留当前配置（取消安全恢复倒计时，规划 4.8.3）。</summary>
    [RelayCommand]
    private void ConfirmKeep()
    {
        var result = _config.ConfirmKeep();
        ReportResult(result, "保留配置");
        RefreshCountdown();
        RefreshSnapshots();
    }

    /// <summary>恢复到选中的配置快照（规划 15.8）。</summary>
    [RelayCommand]
    private void RestoreSnapshot()
    {
        if (SelectedSnapshot is null)
        {
            _app.Dialogs.Warn("请先选择快照", "请在“配置快照”列表中选中要恢复的快照。");
            return;
        }

        var snapshot = SelectedSnapshot;
        if (!_app.Dialogs.Confirm("确认恢复网卡配置",
                $"将把网卡恢复到 {snapshot.CreatedAt:yyyy-MM-dd HH:mm:ss} 的快照。\n\n" +
                $"操作：{snapshot.Operation}\n原配置：{snapshot.Snapshot.SummaryText}\n\n" +
                "⚠ 恢复同样会立即影响网络连接，写操作由提权助手执行（UAC）。\n\n确认恢复吗？")) return;

        var result = _config.Restore(snapshot);
        ReportResult(result, "恢复快照");
        RefreshConfiguredAdapters();
        RefreshSnapshots();
    }

    [RelayCommand]
    private void RefreshSnapshots()
    {
        var adapterId = SelectedConfigAdapter?.Id;
        Snapshots.Clear();
        foreach (var snapshot in _config.ListSnapshots(adapterId)) Snapshots.Add(snapshot);
    }

    /// <summary>启动时检测未决快照：上次修改网卡后没有确认保留（规划 15.8 第 6 步）。</summary>
    private void WarnAboutPendingSnapshot()
    {
        var pending = _config.FindPendingSnapshot();
        if (pending is null) return;

        _app.Log.Warning("NetConfig",
            $"检测到未决网卡配置快照（{pending.FileName}，操作 {pending.Operation}）：上次修改后未确认保留");

        if (_app.Dialogs.Confirm("检测到未决的网卡配置快照",
                $"上次修改网卡后没有确认保留（快照时间 {pending.CreatedAt:yyyy-MM-dd HH:mm:ss}，原配置：{pending.Snapshot.SummaryText}）。\n\n" +
                "是否现在恢复到这个快照？\n（选择“否”将保留当前配置，稍后可在“配置快照”列表中手动恢复。）"))
        {
            var result = _config.Restore(pending);
            ReportResult(result, "恢复未决快照");
            RefreshConfiguredAdapters();
            RefreshSnapshots();
        }
    }

    // ------------------------------------------------------------------ 网络配置方案（规划 4.8.4）

    private void RefreshProfiles()
    {
        Profiles.Clear();
        foreach (var profile in _app.Settings.NetworkProfiles.OrderByDescending(p => p.UpdatedAt)) Profiles.Add(profile);
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (SelectedConfigAdapter is null)
        {
            _app.Dialogs.Warn("请先选择网卡", "配置方案需要绑定一块网卡（用于记录匹配方式）。");
            return;
        }

        var name = ProfileNameText.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            _app.Dialogs.Warn("请填写方案名称", "方案名称不能为空。");
            return;
        }

        var prefix = NetworkConfigValidator.ParsePrefixLength(ConfigPrefixText);
        var profile = new NetworkProfile
        {
            Name = name,
            AdapterMatcher = SelectedConfigAdapter.Id,
            DhcpEnabled = UseDhcp,
            Ipv4 = string.IsNullOrWhiteSpace(ConfigIpv4Text) ? null : ConfigIpv4Text.Trim(),
            PrefixLength = prefix.IsSuccess ? prefix.Value : 24,
            Gateway = ConfigGatewayText.Trim(),
            Dns1 = ConfigDns1Text.Trim(),
            Dns2 = ConfigDns2Text.Trim(),
            ApplyDns = ApplyDns,
            Notes = $"网卡 {SelectedConfigAdapter.Name}",
            UpdatedAt = DateTimeOffset.Now
        };

        var profiles = _app.Settings.NetworkProfiles.Where(p => p.Name != name).ToList();
        profiles.Add(profile);
        var error = _app.Update(s => s with { NetworkProfiles = profiles });
        if (!string.IsNullOrEmpty(error))
        {
            _app.Dialogs.Error("保存方案失败", error);
            return;
        }

        _app.Log.Info("NetConfig", $"已保存网卡配置方案「{name}」（网卡 {SelectedConfigAdapter.Name}）");
        RefreshProfiles();
        StatusMessage = $"已保存网卡配置方案「{name}」。";
    }

    [RelayCommand]
    private void LoadProfile()
    {
        if (SelectedProfile is null) return;

        // 方案保存的是“适配器 Id”，换机后可能对不上：能对上就自动选中，否则提示。
        var match = ConfigAdapters.FirstOrDefault(a => a.Id == SelectedProfile.AdapterMatcher);
        if (match is not null) SelectedConfigAdapter = match;

        UseDhcp = SelectedProfile.DhcpEnabled;
        ConfigIpv4Text = SelectedProfile.Ipv4 ?? ConfigIpv4Text;
        ConfigPrefixText = SelectedProfile.PrefixLength.ToString();
        ConfigGatewayText = SelectedProfile.Gateway ?? string.Empty;
        ConfigDns1Text = SelectedProfile.Dns1 ?? string.Empty;
        ConfigDns2Text = SelectedProfile.Dns2 ?? string.Empty;
        ApplyDns = SelectedProfile.ApplyDns;
        ProfileNameText = SelectedProfile.Name;

        StatusMessage = match is null
            ? $"方案「{SelectedProfile.Name}」已载入编辑区，但它绑定的网卡在此机器上不存在，请重新选择网卡后再应用。"
            : $"方案「{SelectedProfile.Name}」已载入编辑区，请先“校验并预览”再应用。";
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile is null) return;
        if (!_app.Dialogs.Confirm("删除方案", $"确定删除网卡配置方案「{SelectedProfile.Name}」吗？")) return;

        var id = SelectedProfile.Id;
        var profiles = _app.Settings.NetworkProfiles.Where(p => p.Id != id).ToList();
        _app.Update(s => s with { NetworkProfiles = profiles });
        _app.Log.Info("NetConfig", $"已删除网卡配置方案「{SelectedProfile.Name}」");
        RefreshProfiles();
    }

    // ------------------------------------------------------------------ 结果与倒计时

    private void ReportResult(NetConfigResult result, string operation)
    {
        if (result.IsSuccess)
        {
            StatusMessage = $"{operation}：{result.Message ?? "成功"}";
            _app.Log.Info("NetConfig", $"{operation}成功：{result.Message}" +
                                        (result.Readback is null ? string.Empty : $"；回读 {result.Readback.SummaryText}"));
            _app.Dialogs.Info($"{operation}成功",
                (result.Message ?? "已应用。") +
                (result.Readback is null ? string.Empty : $"\n\n回读到的实际配置：{result.Readback.SummaryText}"));
        }
        else
        {
            StatusMessage = $"{operation}失败：{result.ToUserText()}";
            _app.Log.Error("NetConfig", $"{operation}失败：[{result.ErrorCode}] {result.Message}");
            _app.Dialogs.Error($"{operation}失败",
                result.ToUserText() +
                (result.RolledBack ? "\n\n已自动恢复到修改前的配置（快照保留在配置快照列表中）。" : string.Empty) +
                $"\n\n快照目录：{_config.SnapshotDirectory}");
        }

        RefreshCountdown();
    }

    private void RefreshCountdown()
    {
        if (IsRollbackArmed)
        {
            // 倒计时由提权助手持有（主程序崩溃也会回滚，见规划 12.5）；这里只做展示。
            var status = _config.QueryHelperStatus(SelectedConfigAdapter?.Id);
            if (status.RollbackArmed)
            {
                RollbackText = $"⏳ 等待确认保留：剩余 {status.RollbackSecondsRemaining} 秒；到期将自动恢复到修改前的配置。";
            }
            else
            {
                IsRollbackArmed = false;
                RollbackText = "安全恢复倒计时已结束或已被确认。";
                _countdownTimer.Stop();
                RefreshSnapshots();
            }

            return;
        }

        var pending = Snapshots.FirstOrDefault(s => s.IsPending);
        if (pending is not null)
        {
            IsRollbackArmed = true;
            _countdownTimer.Start();
            RollbackText = "⏳ 存在未确认的配置修改（倒计时由提权助手持有）。";
        }
    }

    /// <summary>本页持有助手客户端与倒计时计时器，主窗口关闭时释放。</summary>
    public void Dispose()
    {
        _countdownTimer.Stop();
        _helper.Dispose();
    }
}
