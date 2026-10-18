using System.Collections.ObjectModel;
using Comm.App.Wpf.Services;
using Comm.Network;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comm.App.Wpf.ViewModels;

/// <summary>网络工具：子网计算 + 本机网卡只读查看（见规划 4.9；网卡写入在 M3 实现）。</summary>
public partial class NetworkToolsViewModel : ObservableObject
{
    private readonly AppServices _app;

    public NetworkToolsViewModel(AppServices app)
    {
        _app = app;
        RefreshAdapters();
        Compute();
    }

    public ObservableCollection<LocalAdapterInfo> Adapters { get; } = new();

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

    [RelayCommand]
    private void RefreshAdapters()
    {
        Adapters.Clear();
        foreach (var adapter in NetworkAdapters.Read()) Adapters.Add(adapter);
        StatusMessage = $"已读取 {Adapters.Count} 个本机网络接口（v0.1 为只读；修改 IP/掩码/网关将在后续版本通过提权助手实现）。";
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
}
