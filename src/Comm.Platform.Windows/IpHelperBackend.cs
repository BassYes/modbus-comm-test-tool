using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Comm.Core.Errors;
using Comm.Network;

namespace Comm.Platform.Windows;

/// <summary>
/// 生产用网卡配置后端：读取走 <see cref="NetworkInterface"/>（普通权限），写入走 IP Helper API（需要管理员权限）。
///
/// 规划依据：
///   4.8.2 —— 静态 IPv4 / 掩码 / 网关 / DHCP 切换 / DNS（可选项）/ 辅助 IP（R-13）；
///   4.8.3 —— 修改前检测权限、禁止静默失败、每次修改前落快照、应用后回读校验；
///   15.8  —— 第 3 步应用后回读，第 4 步不一致则回滚（回滚决策在提权助手 / 配置服务层）；
///   12.5  —— 回滚倒计时由提权助手持有，本类只负责"改"，不持有计时器。因此
///            <see cref="NetworkConfigRequest.AutoRollbackSeconds"/> 在这里只记录日志。
///   AD-07 —— netsh 的输出随系统语言变化，因此 netsh 只用于 IP Helper 没有稳定 API 的两处
///            （DHCP 开关、DNS 改写），并且只看退出码。
///
/// 本类的 public 方法**永不抛异常**：所有失败都以 <see cref="NetConfigResult"/> 表达（规划 15.1）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IpHelperBackend : INetworkAdapterBackend
{
    // Win32 / IP Helper 错误码（netioapi.h、winerror.h）
    private const int ErrorSuccess = 0;
    private const int ErrorFileNotFound = 2;
    private const int ErrorNotFound = 1168;
    private const int ErrorObjectAlreadyExists = 5010;

    // NL_PREFIX_ORIGIN / NL_SUFFIX_ORIGIN（iptypes.h）：Manual = 1
    private const int NlPrefixOriginManual = 1;
    private const int NlSuffixOriginManual = 1;

    // NL_ROUTE_PROTOCOL / NL_ROUTE_ORIGIN（netioapi.h）
    private const int MibIpprotoNetmgmt = 3;
    private const int NlRouteOriginManual = 1;

    /// <summary>IP Helper 结构体的期望大小（x64）；布局不对时宁可报错也不去改网卡。</summary>
    private const int ExpectedUnicastRowSize = 80;
    private const int ExpectedForwardRowSize = 104;

    /// <summary>MIB_UNICASTIPADDRESS_TABLE2 中表体相对表头的偏移（NumEntries 之后按 8 字节对齐）。</summary>
    private const int UnicastTableBodyOffset = 8;

    /// <summary>MIB_IPFORWARD_TABLE2 中表体相对表头的偏移（NumEntries 之后按 8 字节对齐）。</summary>
    private const int ForwardTableBodyOffset = 8;

    private const uint InfiniteLifetime = 0xFFFFFFFF;

    private readonly Action<string, string, string?>? _log;

    /// <summary>创建后端。<paramref name="log"/> 为可选的日志回调（格式：级别、分类、正文），库内不使用 Console。</summary>
    public IpHelperBackend(Action<string, string, string?>? log = null)
    {
        _log = log;
        if (!LayoutVerified)
        {
            Log("Error", $"IP Helper 结构体布局校验失败（单播行 {Marshal.SizeOf<MIB_UNICASTIPADDRESS_ROW>()} 字节，" +
                          $"期望 {ExpectedUnicastRowSize}；路由行 {Marshal.SizeOf<MIB_IPFORWARD_ROW2>()} 字节，" +
                          $"期望 {ExpectedForwardRowSize}），本后端将拒绝任何写操作");
        }
    }

    /// <summary>结构体布局自检：布局不对时宁可整条写路径拒绝服务，也不要把错误的前缀长度写到真实网卡上。</summary>
    private static bool LayoutVerified =>
        Marshal.SizeOf<MIB_UNICASTIPADDRESS_ROW>() == ExpectedUnicastRowSize &&
        Marshal.SizeOf<MIB_IPFORWARD_ROW2>() == ExpectedForwardRowSize;

    // -----------------------------------------------------------------------------------------
    // 读取（无需管理员权限）
    // -----------------------------------------------------------------------------------------

    /// <inheritdoc />
    public IReadOnlyList<AdapterSnapshot> ReadAdapters()
    {
        try
        {
            return AdapterReadback.ReadAll(_log);
        }
        catch (Exception ex)
        {
            Log("Error", $"枚举网卡异常：{ex.Message}");
            return Array.Empty<AdapterSnapshot>();
        }
    }

    /// <inheritdoc />
    public AdapterSnapshot? ReadAdapter(string adapterId)
    {
        try
        {
            return AdapterReadback.ReadOne(adapterId, _log);
        }
        catch (Exception ex)
        {
            Log("Error", $"读取网卡 {adapterId} 异常：{ex.Message}");
            return null;
        }
    }

    // -----------------------------------------------------------------------------------------
    // 写入（需要管理员权限）
    // -----------------------------------------------------------------------------------------

    /// <inheritdoc />
    public NetConfigResult Apply(string adapterId, NetworkConfigRequest request)
    {
        try
        {
            var adapter = AdapterReadback.Find(adapterId);
            if (adapter is null) return AdapterNotFound(adapterId);
            if (!LayoutVerified) return LayoutFailure(ErrorCodes.PlatformNetConfigApplyFailed);

            var before = AdapterReadback.TryRead(adapter, _log);
            var props = adapter.GetIPProperties();
            uint index = AdapterReadback.TryGetInterfaceIndex(props);
            ulong luid = ResolveInterfaceLuid(adapter);
            if (index == 0 && luid == 0)
            {
                return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed,
                    $"无法解析网卡 {adapter.Name} 的接口索引/LUID，无法写入配置", before);
            }

            Log("Information", $"开始应用网卡配置：{adapter.Name}（{adapterId}），请求 {(request.DhcpEnabled ? "DHCP" : $"{request.Address}/{request.PrefixLength}")}");
            if (request.AutoRollbackSeconds > 0)
            {
                // 规划 12.5 / D-6：安全恢复倒计时由提权助手独立持有，即使主程序崩溃也会恢复。
                Log("Information", $"安全恢复倒计时 {request.AutoRollbackSeconds} 秒由提权助手持有（本后端不计时）");
            }

            var errors = new List<string>();

            if (request.DhcpEnabled)
            {
                SwitchToDhcp(adapter, errors);
                if (request.ApplyDns) ApplyDns(adapter, request.Dns1, request.Dns2, errors);
            }
            else
            {
                if (!SubnetCalculator.TryParseAddress(request.Address, out var parsed))
                {
                    return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed,
                        $"IP 地址 \"{request.Address}\" 无法解析，未做任何修改", before);
                }

                if (request.PrefixLength is < 0 or > 32)
                {
                    return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed,
                        $"前缀长度 {request.PrefixLength} 非法（应在 0 ~ 32 之间），未做任何修改", before);
                }

                var target = SubnetCalculator.Format(parsed);

                // 后端也会被提权助手直接调用，不能假设上层已经校验过网关格式（否则会写出 0.0.0.0 网关）。
                if (!request.ClearGateway && !string.IsNullOrWhiteSpace(request.Gateway) &&
                    !SubnetCalculator.TryParseAddress(request.Gateway, out _))
                {
                    return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed,
                        $"默认网关 \"{request.Gateway}\" 无法解析，未做任何修改", before);
                }

                ApplyStaticAddress(adapter, before, luid, index, target, request.PrefixLength,
                    request.ReplaceExistingAddresses, errors);

                if (request.ClearGateway)
                {
                    DeleteDefaultGateways(luid, index, errors);
                }
                else if (!string.IsNullOrWhiteSpace(request.Gateway))
                {
                    // 先清掉旧默认路由再加新的：否则会同时存在两个默认网关，回读会出现两条。
                    DeleteDefaultGateways(luid, index, errors);
                    CreateDefaultGateway(luid, index, request.Gateway!, errors);
                }

                if (request.ApplyDns) ApplyDns(adapter, request.Dns1, request.Dns2, errors);
            }

            var readback = AdapterReadback.TryRead(adapter, _log);
            return BuildResult(request, before, readback, errors);
        }
        catch (Exception ex)
        {
            Log("Error", $"应用网卡配置异常：{ex}");
            return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed,
                $"应用网卡配置时发生异常：{ex.Message}", ReadAdapter(adapterId));
        }
    }

    /// <inheritdoc />
    public NetConfigResult Restore(string adapterId, AdapterSnapshot snapshot)
    {
        try
        {
            var adapter = AdapterReadback.Find(adapterId);
            if (adapter is null) return AdapterNotFound(adapterId);
            if (!LayoutVerified) return LayoutFailure(ErrorCodes.PlatformNetConfigRollbackFailed);

            var props = adapter.GetIPProperties();
            uint index = AdapterReadback.TryGetInterfaceIndex(props);
            ulong luid = ResolveInterfaceLuid(adapter);
            if (index == 0 && luid == 0)
            {
                return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigRollbackFailed,
                    $"无法解析网卡 {adapter.Name} 的接口索引/LUID，回滚未执行");
            }

            Log("Information", $"开始回滚网卡配置：{adapter.Name}（目标 {snapshot.SummaryText}）");
            var errors = new List<string>();

            // 第 1 步：先按快照恢复 DHCP 状态——静态模式下的地址与 DHCP 租约地址不能混在一起补。
            if (snapshot.DhcpEnabled)
            {
                var current = AdapterReadback.TryRead(adapter, _log);
                if (current?.DhcpEnabled != true) SwitchToDhcp(adapter, errors);
            }

            var now = AdapterReadback.TryRead(adapter, _log);

            // 第 2 步：删除快照里没有的地址（IP Helper 表与 NetworkInterface 两路取并集，避免漏删）。
            var keep = AdapterReadback.CidrSet(snapshot.Ipv4);
            foreach (var address in EnumerateExistingAddresses(adapter, index, luid, now))
            {
                if (keep.Contains($"{address.Address}/{address.PrefixLength}")) continue;
                DeleteAddress(luid, index, address, errors);
            }

            // 第 3 步：补回快照里的地址（含前缀）。
            foreach (var address in snapshot.Ipv4)
            {
                CreateAddress(luid, index, address.Address, address.PrefixLength, errors);
            }

            // 第 4 步：按快照恢复默认网关（有则建、无则删）。
            DeleteDefaultGateways(luid, index, errors);
            foreach (var gateway in snapshot.Gateways)
            {
                CreateDefaultGateway(luid, index, gateway, errors);
            }

            // 第 5 步：恢复 DNS（仅当快照里确实有 DNS）。
            if (snapshot.DnsServers.Count > 0)
            {
                ApplyDns(adapter, snapshot.DnsServers[0],
                    snapshot.DnsServers.Count > 1 ? snapshot.DnsServers[1] : null, errors);
            }

            var readback = AdapterReadback.TryRead(adapter, _log);
            var diff = NetConfigCompare.ForRestore(snapshot, readback);
            if (errors.Count > 0 || diff is not null)
            {
                var message = string.Join("；", errors.Concat(diff is null ? Array.Empty<string>() : new[] { diff }));
                Log("Error", $"回滚未完全成功：{message}");
                return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigRollbackFailed,
                    $"{message}。请手工核对网卡配置（规划 15.8 第 4 步）", readback);
            }

            Log("Information", "网卡配置已回滚并回读一致");
            return NetConfigResult.Ok(readback, "已恢复到修改前的配置");
        }
        catch (Exception ex)
        {
            Log("Error", $"回滚网卡配置异常：{ex}");
            return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigRollbackFailed,
                $"回滚时发生异常：{ex.Message}，请手工核对网卡配置", ReadAdapter(adapterId));
        }
    }

    /// <inheritdoc />
    public NetConfigResult AddAuxiliaryAddress(string adapterId, string address, int prefixLength)
    {
        try
        {
            var adapter = AdapterReadback.Find(adapterId);
            if (adapter is null) return AdapterNotFound(adapterId);
            if (!LayoutVerified) return LayoutFailure(ErrorCodes.PlatformNetConfigApplyFailed);
            if (!SubnetCalculator.TryParseAddress(address, out var parsed))
            {
                return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed, $"IP 地址 \"{address}\" 无法解析", ReadAdapter(adapterId));
            }

            if (prefixLength is < 0 or > 32)
            {
                return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed, $"前缀长度 {prefixLength} 非法（应在 0 ~ 32 之间）", ReadAdapter(adapterId));
            }

            var props = adapter.GetIPProperties();
            uint index = AdapterReadback.TryGetInterfaceIndex(props);
            ulong luid = ResolveInterfaceLuid(adapter);
            if (index == 0 && luid == 0)
            {
                return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed,
                    $"无法解析网卡 {adapter.Name} 的接口索引/LUID", ReadAdapter(adapterId));
            }

            var before = AdapterReadback.TryRead(adapter, _log);
            var target = SubnetCalculator.Format(parsed);
            Log("Information", $"添加辅助 IP：{adapter.Name} ← {target}/{prefixLength}");

            var errors = new List<string>();
            CreateAddress(luid, index, target, prefixLength, errors);

            var readback = AdapterReadback.TryRead(adapter, _log);
            if (errors.Count > 0)
            {
                return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed, string.Join("；", errors), readback);
            }

            // 辅助 IP 只增加，不动其他地址（R-13）。
            var expected = before is null
                ? new[] { $"{target}/{prefixLength}" }
                : AdapterReadback.CidrSet(before.Ipv4).Append($"{target}/{prefixLength}").ToArray();
            var diff = NetConfigCompare.ForAddressSet(expected, readback, "添加辅助 IP");
            return diff is null
                ? NetConfigResult.Ok(readback, $"已添加辅助 IP {target}/{prefixLength}（原有地址保持不变）")
                : NetConfigResult.Fail(ErrorCodes.PlatformNetConfigReadbackMismatch, diff, readback);
        }
        catch (Exception ex)
        {
            Log("Error", $"添加辅助 IP 异常：{ex}");
            return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed,
                $"添加辅助 IP 时发生异常：{ex.Message}", ReadAdapter(adapterId));
        }
    }

    /// <inheritdoc />
    public NetConfigResult RemoveAuxiliaryAddress(string adapterId, string address)
    {
        try
        {
            var adapter = AdapterReadback.Find(adapterId);
            if (adapter is null) return AdapterNotFound(adapterId);
            if (!LayoutVerified) return LayoutFailure(ErrorCodes.PlatformNetConfigApplyFailed);

            var before = AdapterReadback.TryRead(adapter, _log);
            if (before is null)
            {
                return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigReadbackMismatch,
                    $"无法读取网卡 {adapter.Name} 的当前地址，未做任何修改");
            }

            var targets = before.Ipv4
                .Where(a => string.Equals(a.Address, address, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (targets.Length == 0)
            {
                // 幂等：地址本来就不在网卡上，视为成功（用户可能已经手工删除）。
                return NetConfigResult.Ok(before, $"网卡上没有地址 {address}，无需移除");
            }

            var props = adapter.GetIPProperties();
            uint index = AdapterReadback.TryGetInterfaceIndex(props);
            ulong luid = ResolveInterfaceLuid(adapter);
            if (index == 0 && luid == 0)
            {
                return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed,
                    $"无法解析网卡 {adapter.Name} 的接口索引/LUID", before);
            }

            Log("Information", $"移除辅助 IP：{adapter.Name} ← {address}");
            var errors = new List<string>();
            foreach (var target in targets) DeleteAddress(luid, index, target, errors);

            var readback = AdapterReadback.TryRead(adapter, _log);
            if (errors.Count > 0)
            {
                return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed, string.Join("；", errors), readback);
            }

            var expected = AdapterReadback.CidrSet(before.Ipv4)
                .Where(cidr => !cidr.StartsWith(address + "/", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var diff = NetConfigCompare.ForAddressSet(expected, readback, "移除辅助 IP");
            return diff is null
                ? NetConfigResult.Ok(readback, $"已移除地址 {address}（其他地址未受影响）")
                : NetConfigResult.Fail(ErrorCodes.PlatformNetConfigReadbackMismatch, diff, readback);
        }
        catch (Exception ex)
        {
            Log("Error", $"移除辅助 IP 异常：{ex}");
            return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed,
                $"移除辅助 IP 时发生异常：{ex.Message}", ReadAdapter(adapterId));
        }
    }

    // -----------------------------------------------------------------------------------------
    // 结果组装与回读校验
    // -----------------------------------------------------------------------------------------

    private NetConfigResult BuildResult(NetworkConfigRequest request, AdapterSnapshot? before,
        AdapterSnapshot? readback, List<string> errors)
    {
        // 回读不一致（PLT-5005，规划 15.8 第 3/4 步）
        var diff = NetConfigCompare.ForApply(request, before, readback);

        if (errors.Count > 0 && diff is not null)
        {
            return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigReadbackMismatch,
                string.Join("；", errors) + "；" + diff, readback);
        }

        if (errors.Count > 0)
        {
            return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed, string.Join("；", errors), readback);
        }

        if (diff is not null)
        {
            Log("Warning", $"回读不一致：{diff}");
            return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigReadbackMismatch, diff, readback);
        }

        Log("Information", $"网卡配置已应用并回读一致：{readback?.SummaryText}");
        return NetConfigResult.Ok(readback, "已应用：" + (readback?.SummaryText ?? readback?.Id));
    }

    private NetConfigResult AdapterNotFound(string adapterId) => NetConfigResult.Fail(
        ErrorCodes.PlatformAdapterNotFound,
        $"找不到网卡 {adapterId}：可能已被拔出、禁用或重命名，请刷新网卡列表后重试");

    private static NetConfigResult LayoutFailure(string errorCode) => NetConfigResult.Fail(
        errorCode,
        $"IP Helper 结构体布局校验失败（x64 下单播行应为 {ExpectedUnicastRowSize} 字节、路由行应为 {ExpectedForwardRowSize} 字节），" +
        "为避免把错误的前缀长度写到网卡上，本次写操作已放弃；请改用 netsh 后端或提权助手");

    // -----------------------------------------------------------------------------------------
    // 静态地址 / DHCP / DNS
    // -----------------------------------------------------------------------------------------

    /// <summary>把目标静态地址写到网卡上（ReplaceExistingAddresses = false 时等价于"叠加辅助 IP"）。</summary>
    private void ApplyStaticAddress(NetworkInterface adapter, AdapterSnapshot? before, ulong luid, uint index,
        string target, int prefixLength, bool replaceExistingAddresses, List<string> errors)
    {
        // DHCP 租约地址不能直接用 IP Helper 删除：必须先退出 DHCP，
        // 而"退出 DHCP 并改成静态"唯一的稳定入口是 netsh（IP Helper 没有 DHCP 开关 API，见下方 SwitchToDhcp 注释）。
        if (before?.DhcpEnabled == true)
        {
            bool ok = NetshCommand.Run(_log, out _, out var failure,
                "interface", "ipv4", "set", "address",
                NetshCommand.NameArgument(adapter.Name), "source=static",
                $"address={target}", $"mask={AdapterReadback.MaskText(prefixLength)}");
            if (!ok) errors.Add(failure ?? "切换到静态地址失败");
        }

        var current = AdapterReadback.TryRead(adapter, _log) ?? before;

        if (replaceExistingAddresses && current is not null)
        {
            foreach (var existing in current.Ipv4)
            {
                // 已经是目标地址+前缀的条目不要删（避免网络闪断后又要重建）。
                if (string.Equals(existing.Address, target, StringComparison.OrdinalIgnoreCase) &&
                    existing.PrefixLength == prefixLength) continue;
                DeleteAddress(luid, index, existing, errors);
            }
        }

        CreateAddress(luid, index, target, prefixLength, errors);
    }

    /// <summary>
    /// 切换到 DHCP。
    /// 为什么不走 IP Helper：DHCP 由 DHCP 客户端服务（Dnscache/Dhcp）驱动，IP Helper 只暴露"某个地址条目"，
    /// 没有"启用/禁用 DHCP"的公开 API；netsh 是微软文档化的唯一稳定入口。
    /// 依照 AD-07，本方法只看退出码，绝不解析 netsh 文本（输出随系统语言变化）。
    /// </summary>
    private void SwitchToDhcp(NetworkInterface adapter, List<string> errors)
    {
        bool ok = NetshCommand.Run(_log, out _, out var failure,
            "interface", "ipv4", "set", "address", NetshCommand.NameArgument(adapter.Name), "source=dhcp");
        if (!ok) errors.Add(failure ?? "切换到 DHCP 失败");
    }

    /// <summary>
    /// 改写 DNS（规划 4.8.2：DNS 为可选项，需单独确认）。
    /// 同样走 netsh：IP Helper 的 DNS 接口（SetInterfaceDnsSettings）只在 Win10 1809+ 可用且语义与 netsh 不同，
    /// 为保持与 <see cref="NetshBackend"/> 一致，这里统一用 netsh + 退出码。
    /// validate=no 用于跳过 netsh 的 DNS 可达性校验（否则 DHCP 尚未就绪时可能长时间阻塞）。
    /// </summary>
    private void ApplyDns(NetworkInterface adapter, string? dns1, string? dns2, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(dns1)) return;

        bool ok = NetshCommand.Run(_log, out _, out var failure,
            "interface", "ipv4", "set", "dns", NetshCommand.NameArgument(adapter.Name),
            "static", dns1, "primary", "validate=no");
        if (!ok) errors.Add(failure ?? $"设置首选 DNS {dns1} 失败");

        if (string.IsNullOrWhiteSpace(dns2)) return;

        ok = NetshCommand.Run(_log, out _, out failure,
            "interface", "ipv4", "add", "dns", NetshCommand.NameArgument(adapter.Name),
            dns2, "index=2", "validate=no");
        if (!ok) errors.Add(failure ?? $"设置备用 DNS {dns2} 失败");
    }

    // -----------------------------------------------------------------------------------------
    // IP Helper：单播地址
    // -----------------------------------------------------------------------------------------

    private void CreateAddress(ulong luid, uint index, string address, int prefixLength, List<string> errors)
    {
        var row = NewUnicastRow(luid, index, address, prefixLength);
        int rc = CreateUnicastIpAddressEntry(ref row);

        // 5010 = ERROR_OBJECT_ALREADY_EXISTS：地址已经存在，视为幂等成功（重复应用同一方案不报错）。
        if (rc is ErrorSuccess or ErrorObjectAlreadyExists)
        {
            Log("Debug", $"CreateUnicastIpAddressEntry {address}/{prefixLength} → {NetshCommand.Describe(rc)}");
            return;
        }

        errors.Add($"添加地址 {address}/{prefixLength} 失败：{NetshCommand.Describe(rc)}（错误码 {rc}）");
    }

    private void DeleteAddress(ulong luid, uint index, Ipv4AddressInfo address, List<string> errors)
    {
        var row = NewUnicastRow(luid, index, address.Address, address.PrefixLength);
        int rc = DeleteUnicastIpAddressEntry(ref row);

        // 1168 = ERROR_NOT_FOUND：本来就没有，视为幂等成功。
        if (rc is ErrorSuccess or ErrorNotFound)
        {
            Log("Debug", $"DeleteUnicastIpAddressEntry {address.Cidr} → {NetshCommand.Describe(rc)}");
            return;
        }

        errors.Add($"删除地址 {address.Cidr} 失败：{NetshCommand.Describe(rc)}（错误码 {rc}）");
    }

    private MIB_UNICASTIPADDRESS_ROW NewUnicastRow(ulong luid, uint index, string address, int prefixLength)
    {
        var row = default(MIB_UNICASTIPADDRESS_ROW);
        InitializeUnicastIpAddressEntry(ref row);

        row.InterfaceLuid = luid;
        row.InterfaceIndex = index;
        row.Address.Family = (ushort)AdapterReadback.AfInet;
        row.Address.Ipv4Address = ToNetworkUInt32(address);
        row.OnLinkPrefixLength = (uint)prefixLength;
        row.PrefixOrigin = NlPrefixOriginManual;
        row.SuffixOrigin = NlSuffixOriginManual;
        row.ValidLifetime = InfiniteLifetime;
        row.PreferredLifetime = InfiniteLifetime;
        // DadState 故意不改：留给协议栈做重复地址检测（DAD），手工改成 Preferred 会跳过冲突检测。
        return row;
    }

    /// <summary>
    /// 用 IP Helper 枚举某网卡当前的 IPv4 地址（作为 <see cref="NetworkInterface"/> 之外的补充来源，
    /// 用于回滚时决定"要删哪些地址"）。任何异常或可疑数据都退回空集合，绝不因此报错。
    /// </summary>
    private List<Ipv4AddressInfo> ReadAddressTable(uint index, ulong luid)
    {
        var list = new List<Ipv4AddressInfo>();
        IntPtr table = IntPtr.Zero;

        try
        {
            if (GetUnicastIpAddressTable(AdapterReadback.AfInet, out table) != ErrorSuccess || table == IntPtr.Zero)
            {
                return list;
            }

            int count = Marshal.ReadInt32(table);
            if (count is <= 0 or > 4096) return list;

            int rowSize = Marshal.SizeOf<MIB_UNICASTIPADDRESS_ROW>();
            // MIB_UNICASTIPADDRESS_TABLE2 = { ULONG NumEntries; MIB_UNICASTIPADDRESS_ROW Table[ANY_SIZE]; }
            // 行内含有 8 字节对齐的 NET_LUID，所以表体从偏移 8 开始（x64 与 x86 都是 8）。
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_UNICASTIPADDRESS_ROW>(
                    IntPtr.Add(table, UnicastTableBodyOffset + (i * rowSize)));

                if (row.Address.Family != AdapterReadback.AfInet) continue;
                if (row.OnLinkPrefixLength > 32) continue;
                bool sameInterface = (index != 0 && row.InterfaceIndex == index) || (luid != 0 && row.InterfaceLuid == luid);
                if (!sameInterface) continue;

                uint value = FromNetworkUInt32(row.Address.Ipv4Address);
                int prefix = (int)row.OnLinkPrefixLength;
                list.Add(new Ipv4AddressInfo(SubnetCalculator.Format(value), prefix, AdapterReadback.MaskText(prefix)));
            }
        }
        catch (Exception ex)
        {
            Log("Warning", $"读取 IP Helper 地址表失败，退回 NetworkInterface 数据：{ex.Message}");
            return new List<Ipv4AddressInfo>();
        }
        finally
        {
            if (table != IntPtr.Zero) FreeMibTable(table);
        }

        return list;
    }

    /// <summary>回滚时的"现存地址"来源：NetworkInterface 与 IP Helper 地址表取并集（去重）。</summary>
    private IEnumerable<Ipv4AddressInfo> EnumerateExistingAddresses(NetworkInterface adapter, uint index, ulong luid,
        AdapterSnapshot? current)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var address in current?.Ipv4 ?? Array.Empty<Ipv4AddressInfo>())
        {
            if (seen.Add(address.Cidr)) yield return address;
        }

        foreach (var address in ReadAddressTable(index, luid))
        {
            if (seen.Add(address.Cidr)) yield return address;
        }
    }

    // -----------------------------------------------------------------------------------------
    // IP Helper：默认网关（IPv4 路由表）
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// 删除该网卡上的默认路由（0.0.0.0/0）。
    /// <para>
    /// <c>DeleteIpForwardEntry2</c> 按"接口 + 目标前缀 + 下一跳"精确匹配，因此不能用一个
    /// NextHop = 0.0.0.0 的通配行删除真实网关：这里先用 <c>GetIpForwardTable2</c> 枚举出该网卡上的
    /// 默认路由，再逐条删除。枚举失败时才退回一次通配删除（尽力而为，不静默失败）。
    /// </para>
    /// </summary>
    private void DeleteDefaultGateways(ulong luid, uint index, List<string> errors)
    {
        var routes = ReadDefaultRoutes(luid, index);
        if (routes.Count == 0)
        {
            var wildcard = NewForwardRow(luid, index, gateway: null);
            int rc = DeleteIpForwardEntry2(ref wildcard);
            if (rc is ErrorSuccess or ErrorNotFound or ErrorFileNotFound) return;

            errors.Add($"删除默认网关失败：{NetshCommand.Describe(rc)}（错误码 {rc}）");
            return;
        }

        foreach (var route in routes)
        {
            var row = route;
            int rc = DeleteIpForwardEntry2(ref row);
            if (rc is ErrorSuccess or ErrorNotFound or ErrorFileNotFound) continue;

            errors.Add($"删除默认网关失败：{NetshCommand.Describe(rc)}（错误码 {rc}）");
        }
    }

    /// <summary>枚举某网卡上的 IPv4 默认路由（目标前缀长度 0）。任何异常都退回空集合。</summary>
    private List<MIB_IPFORWARD_ROW2> ReadDefaultRoutes(ulong luid, uint index)
    {
        var list = new List<MIB_IPFORWARD_ROW2>();
        IntPtr table = IntPtr.Zero;

        try
        {
            if (GetIpForwardTable2(AdapterReadback.AfInet, out table) != ErrorSuccess || table == IntPtr.Zero)
            {
                return list;
            }

            int count = Marshal.ReadInt32(table);
            if (count is <= 0 or > 4096) return list;

            int rowSize = Marshal.SizeOf<MIB_IPFORWARD_ROW2>();
            // MIB_IPFORWARD_TABLE2 = { ULONG NumEntries; MIB_IPFORWARD_ROW2 Table[ANY_SIZE]; }
            // MIB_IPFORWARD_ROW2 含 8 字节对齐的 NET_LUID，因此表体从偏移 8 开始。
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_IPFORWARD_ROW2>(
                    IntPtr.Add(table, ForwardTableBodyOffset + (i * rowSize)));

                if (row.DestinationPrefix.Prefix.Family != AdapterReadback.AfInet) continue;
                if (row.DestinationPrefix.PrefixLength != 0) continue;
                bool sameInterface = (index != 0 && row.InterfaceIndex == index) || (luid != 0 && row.InterfaceLuid == luid);
                if (!sameInterface) continue;

                list.Add(row);
            }
        }
        catch (Exception ex)
        {
            Log("Warning", $"读取 IP Helper 路由表失败，退回通配删除：{ex.Message}");
            return new List<MIB_IPFORWARD_ROW2>();
        }
        finally
        {
            if (table != IntPtr.Zero) FreeMibTable(table);
        }

        return list;
    }

    private void CreateDefaultGateway(ulong luid, uint index, string gateway, List<string> errors)
    {
        var row = NewForwardRow(luid, index, gateway);
        int rc = CreateIpForwardEntry2(ref row);

        if (rc == ErrorSuccess) return;
        if (rc == ErrorObjectAlreadyExists) return; // 幂等

        errors.Add($"设置默认网关 {gateway} 失败：{NetshCommand.Describe(rc)}（错误码 {rc}）");
    }

    private static MIB_IPFORWARD_ROW2 NewForwardRow(ulong luid, uint index, string? gateway)
    {
        var row = default(MIB_IPFORWARD_ROW2);

        row.InterfaceLuid = luid;
        row.InterfaceIndex = index;

        // 目标前缀 0.0.0.0/0 = 默认路由。
        row.DestinationPrefix.Prefix.Family = (ushort)AdapterReadback.AfInet;
        row.DestinationPrefix.Prefix.Ipv4Address = 0;
        row.DestinationPrefix.PrefixLength = 0;

        row.NextHop.Family = (ushort)AdapterReadback.AfInet;
        row.NextHop.Ipv4Address = gateway is null ? 0 : ToNetworkUInt32(gateway);

        row.ValidLifetime = InfiniteLifetime;
        row.PreferredLifetime = InfiniteLifetime;
        row.Metric = 0; // 0 = 由系统按接口速度自动计算度量
        row.Protocol = MibIpprotoNetmgmt;
        row.Origin = NlRouteOriginManual;
        row.Loopback = 0;
        row.AutoconfigureAddress = 1;
        row.Publish = 0;
        row.Immortal = 1;
        row.Age = 0;
        return row;
    }

    // -----------------------------------------------------------------------------------------
    // 网卡标识与地址字节序
    // -----------------------------------------------------------------------------------------

    /// <summary>网卡 GUID（<see cref="NetworkInterface.Id"/>）→ NET_LUID；失败返回 0（调用方退回 InterfaceIndex）。</summary>
    private ulong ResolveInterfaceLuid(NetworkInterface adapter)
    {
        try
        {
            if (!Guid.TryParse(adapter.Id, out var guid))
            {
                // NetworkInterface.Id 在 Windows 上是带大括号的 GUID 字符串，理论上不会走到这里。
                return 0;
            }

            return ConvertInterfaceGuidToLuid(ref guid, out ulong luid) == ErrorSuccess ? luid : 0;
        }
        catch (Exception ex)
        {
            Log("Warning", $"解析网卡 {adapter.Name} 的 LUID 失败：{ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 把点分十进制地址转成 <c>SOCKADDR_IN.sin_addr</c> 需要的形式。
    /// sin_addr 在内存中必须是**网络字节序**（第一个字节 = 第一段），因此这里按主机字节序反转一次；
    /// 若直接写入"逻辑值"（a&lt;&lt;24|b&lt;&lt;16|c&lt;&lt;8|d），在 x86/x64 上会得到反向地址。
    /// </summary>
    private static uint ToNetworkUInt32(string address)
    {
        uint logical = ParseLogical(address);
        return BitConverter.IsLittleEndian ? System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(logical) : logical;
    }

    /// <summary><c>SOCKADDR_IN.sin_addr</c> → 点分十进制逻辑值（<see cref="ToNetworkUInt32"/> 的逆运算）。</summary>
    private static uint FromNetworkUInt32(uint value)
    {
        return BitConverter.IsLittleEndian ? System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value) : value;
    }

    private static uint ParseLogical(string address) =>
        SubnetCalculator.TryParseAddress(address, out var value) ? value : 0;

    private void Log(string level, string message) => _log?.Invoke(level, "NetConfig", message);

    // -----------------------------------------------------------------------------------------
    // P/Invoke（netioapi.h / iphlpapi.dll）
    //
    // 结构体布局按 x64 逐字段对齐（自然对齐，默认 Pack=8）。下面的偏移是在真实 Windows 上读
    // GetUnicastIpAddressTable / GetIpForwardTable2 返回的表体后逐字段核对出来的（尺寸不对时
    // 构造函数会告警，所有写操作直接拒绝，见 LayoutFailure）：
    //
    //   MIB_UNICASTIPADDRESS_ROW = 80 字节
    //     0  SOCKADDR_INET Address（28）
    //     28 4 字节对齐填充/CompartmentId（保持 0）
    //     32 NET_LUID InterfaceLuid
    //     40 NET_IFINDEX InterfaceIndex
    //     44 NL_PREFIX_ORIGIN / 48 NL_SUFFIX_ORIGIN
    //     52 ValidLifetime / 56 PreferredLifetime
    //     60 OnLinkPrefixLength（C 侧为 4 字节，前 1 字节才是前缀长度）
    //     64 NL_DAD_STATE / 68 SCOPE_ID / 72 系统保留 8 字节
    //
    //   MIB_IPFORWARD_ROW2       = 104 字节
    //     0  NET_LUID / 8 InterfaceIndex
    //     12 IP_ADDRESS_PREFIX DestinationPrefix（32）
    //     44 SOCKADDR_INET NextHop（28）
    //     72 SitePrefixLength / 76 ValidLifetime / 80 PreferredLifetime / 84 Metric
    //     88 NL_ROUTE_PROTOCOL / 92..95 四个 BOOLEAN / 96 Age / 100 NL_ROUTE_ORIGIN
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// <c>SOCKADDR_INET</c>：SOCKADDR_IN（16 字节）与 SOCKADDR_IN6（28 字节）的联合体，对齐 4、大小 28。
    /// 这里刻意全部用 4 字节字段描述 IPv6 部分：C 侧 sin6_addr 是 UCHAR[16]、sin6_scope_id 是 ULONG，
    /// 联合体对齐只有 4；若用 ulong 表示，.NET 会把对齐提到 8，进而把后续字段（LUID）推到错误偏移。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    internal struct SOCKADDR_INET
    {
        /// <summary>AF_INET(2) / AF_INET6(23)。</summary>
        [FieldOffset(0)] public ushort Family;

        [FieldOffset(2)] public ushort Port;

        /// <summary>IPv4 地址；内存中为网络字节序（见 <see cref="ToNetworkUInt32"/>）。</summary>
        [FieldOffset(4)] public uint Ipv4Address;

        /// <summary>sin6_flowinfo（仅用于占位，本类不写 IPv6）。</summary>
        [FieldOffset(8)] public uint Ipv6FlowInfo;

        /// <summary>sin6_addr 第 1 个 4 字节组（仅用于占位）。</summary>
        [FieldOffset(12)] public uint Ipv6Address0;

        /// <summary>sin6_addr 第 2 个 4 字节组（仅用于占位）。</summary>
        [FieldOffset(16)] public uint Ipv6Address1;

        /// <summary>sin6_addr 第 3 个 4 字节组（仅用于占位）。</summary>
        [FieldOffset(20)] public uint Ipv6Address2;

        /// <summary>sin6_scope_id（也是 sin6_addr 第 4 个 4 字节组；仅用于占位）。</summary>
        [FieldOffset(24)] public uint Ipv6ScopeId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_UNICASTIPADDRESS_ROW
    {
        public SOCKADDR_INET Address;   // 0  （28 字节，对齐 4）
        public uint Reserved0;          // 28（SOCKADDR_INET 之后的 4 字节填充/CompartmentId，保持 0）
        public ulong InterfaceLuid;     // 32
        public uint InterfaceIndex;     // 40
        public int PrefixOrigin;        // 44
        public int SuffixOrigin;        // 48
        public uint ValidLifetime;      // 52
        public uint PreferredLifetime;  // 56

        /// <summary>前缀长度；C 侧是 4 字节字段（高 3 字节为 0），按 4 字节读写更稳妥。</summary>
        public uint OnLinkPrefixLength; // 60

        public int DadState;            // 64
        public uint ScopeId;            // 68

        /// <summary>当前 Windows 版本在该行末尾多出的 8 字节（时间戳），交给系统初始化。</summary>
        public ulong Reserved1;         // 72
    }

    /// <summary><c>IP_ADDRESS_PREFIX</c>：前缀 + 前缀长度。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IP_ADDRESS_PREFIX
    {
        public SOCKADDR_INET Prefix;
        public byte PrefixLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_IPFORWARD_ROW2
    {
        public ulong InterfaceLuid;          // 0
        public uint InterfaceIndex;          // 8
        public IP_ADDRESS_PREFIX DestinationPrefix; // 12
        public SOCKADDR_INET NextHop;        // 44
        public byte SitePrefixLength;        // 72
        public uint ValidLifetime;           // 76
        public uint PreferredLifetime;       // 80
        public uint Metric;                  // 84
        public int Protocol;                 // 88
        public byte Loopback;                // 92
        public byte AutoconfigureAddress;    // 93
        public byte Publish;                 // 94
        public byte Immortal;                // 95
        public uint Age;                     // 96
        public int Origin;                   // 100
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern void InitializeUnicastIpAddressEntry(ref MIB_UNICASTIPADDRESS_ROW row);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int CreateUnicastIpAddressEntry(ref MIB_UNICASTIPADDRESS_ROW row);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int DeleteUnicastIpAddressEntry(ref MIB_UNICASTIPADDRESS_ROW row);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetUnicastIpAddressTable(int family, out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int CreateIpForwardEntry2(ref MIB_IPFORWARD_ROW2 row);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int DeleteIpForwardEntry2(ref MIB_IPFORWARD_ROW2 row);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetIpForwardTable2(int family, out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern void FreeMibTable(IntPtr memory);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int ConvertInterfaceGuidToLuid(ref Guid interfaceGuid, out ulong interfaceLuid);
}
