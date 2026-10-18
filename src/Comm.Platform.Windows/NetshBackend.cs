using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using Comm.Core.Errors;
using Comm.Network;

namespace Comm.Platform.Windows;

/// <summary>
/// 降级 / 对照用网卡配置后端：语义与 <see cref="IpHelperBackend"/> 完全一致，但**写入全部走 netsh**。
///
/// 用途（规划 4.8、12.5、16.3.7）：
///   ① IP Helper 在个别系统/驱动组合上会失败（例如某些虚拟网卡拒绝 CreateUnicastIpAddressEntry），
///      此时可以整体降级到本实现，让现场仍能改配置；
///   ② 作为对照实现，便于核对 IP Helper 的写入结果与 netsh 是否一致。
///
/// 约束（AD-07）：netsh 的文本输出随系统语言变化，因此本类**只取退出码**，绝不解析任何输出；
/// 成败一律以"应用后回读"为准（规划 15.8 第 3 步）。
/// public 方法永不抛异常，所有失败以 <see cref="NetConfigResult"/> 表达。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NetshBackend : INetworkAdapterBackend
{
    private readonly Action<string, string, string?>? _log;

    /// <summary>创建后端。<paramref name="log"/> 为可选的日志回调（格式：级别、分类、正文），库内不使用 Console。</summary>
    public NetshBackend(Action<string, string, string?>? log = null)
    {
        _log = log;
    }

    /// <inheritdoc />
    public IReadOnlyList<AdapterSnapshot> ReadAdapters()
    {
        try
        {
            // 读取与 IpHelperBackend 走同一条路径（NetworkInterface），保证两个后端读数一致。
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

    /// <inheritdoc />
    public NetConfigResult Apply(string adapterId, NetworkConfigRequest request)
    {
        try
        {
            var adapter = AdapterReadback.Find(adapterId);
            if (adapter is null) return AdapterNotFound(adapterId);

            var before = AdapterReadback.TryRead(adapter, _log);
            Log("Information", $"开始应用网卡配置（netsh）：{adapter.Name}（{adapterId}）");
            if (request.AutoRollbackSeconds > 0)
            {
                // 规划 12.5：倒计时由提权助手持有，后端不计时。
                Log("Information", $"安全恢复倒计时 {request.AutoRollbackSeconds} 秒由提权助手持有（本后端不计时）");
            }

            var errors = new List<string>();
            string name = NetshCommand.NameArgument(adapter.Name);

            if (request.DhcpEnabled)
            {
                Run(errors, "interface", "ipv4", "set", "address", name, "source=dhcp");
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
                var mask = AdapterReadback.MaskText(request.PrefixLength);

                if (request.ReplaceExistingAddresses)
                {
                    // set address source=static 会一并替换地址并关闭 DHCP，正好对应"替换现有地址"。
                    var arguments = new List<string>
                    {
                        "interface", "ipv4", "set", "address", name, "source=static",
                        $"address={target}", $"mask={mask}"
                    };
                    if (request.ClearGateway || string.IsNullOrWhiteSpace(request.Gateway))
                    {
                        arguments.Add("gateway=none");
                    }
                    else
                    {
                        arguments.Add($"gateway={request.Gateway}");
                        arguments.Add("gwmetric=1");
                    }

                    Run(errors, arguments.ToArray());
                }
                else
                {
                    // 叠加辅助 IP：add address 只在网卡上追加地址，不动现有地址（R-13）。
                    Run(errors, "interface", "ipv4", "add", "address", name,
                        $"address={target}", $"mask={mask}");

                    // 叠加模式下的网关改动与地址无关，单独下发（netsh 允许只改网关）。
                    if (request.ClearGateway)
                    {
                        Run(errors, "interface", "ipv4", "set", "address", name, "gateway=none");
                    }
                    else if (!string.IsNullOrWhiteSpace(request.Gateway))
                    {
                        Run(errors, "interface", "ipv4", "set", "address", name,
                            $"gateway={request.Gateway}", "gwmetric=1");
                    }
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

            Log("Information", $"开始回滚网卡配置（netsh）：{adapter.Name}（目标 {snapshot.SummaryText}）");
            var errors = new List<string>();
            string name = NetshCommand.NameArgument(adapter.Name);

            if (snapshot.DhcpEnabled)
            {
                // DHCP 模式下地址与网关都由 DHCP 客户端给回，只需切回 DHCP 再恢复 DNS。
                Run(errors, "interface", "ipv4", "set", "address", name, "source=dhcp");
                if (snapshot.DnsServers.Count > 0)
                {
                    ApplyDns(adapter, snapshot.DnsServers[0],
                        snapshot.DnsServers.Count > 1 ? snapshot.DnsServers[1] : null, errors);
                }
                else
                {
                    Run(errors, "interface", "ipv4", "set", "dns", name, "source=dhcp");
                }
            }
            else
            {
                var now = AdapterReadback.TryRead(adapter, _log);

                // 第 1 步：删除快照里没有的地址。
                var keep = AdapterReadback.CidrSet(snapshot.Ipv4);
                foreach (var address in now?.Ipv4 ?? Array.Empty<Ipv4AddressInfo>())
                {
                    if (keep.Contains(address.Cidr)) continue;
                    Run(errors, "interface", "ipv4", "delete", "address", name,
                        $"address={address.Address}", $"mask={address.Mask}");
                }

                // 第 2 步：补回快照里的地址，并把网关设回快照值（有则设、无则清）。
                var existing = AdapterReadback.CidrSet(now?.Ipv4 ?? Array.Empty<Ipv4AddressInfo>());
                foreach (var address in snapshot.Ipv4)
                {
                    if (existing.Contains(address.Cidr)) continue;
                    Run(errors, "interface", "ipv4", "add", "address", name,
                        $"address={address.Address}", $"mask={address.Mask}");
                }

                var first = snapshot.Ipv4.FirstOrDefault();
                if (first is not null)
                {
                    var arguments = new List<string>
                    {
                        "interface", "ipv4", "set", "address", name, "source=static",
                        $"address={first.Address}", $"mask={first.Mask}"
                    };
                    if (snapshot.Gateways.Count == 0)
                    {
                        arguments.Add("gateway=none");
                    }
                    else
                    {
                        arguments.Add($"gateway={snapshot.Gateways[0]}");
                        arguments.Add("gwmetric=1");
                    }

                    Run(errors, arguments.ToArray());
                }

                // 第 3 步：恢复 DNS（仅当快照里确实有 DNS）。
                if (snapshot.DnsServers.Count > 0)
                {
                    ApplyDns(adapter, snapshot.DnsServers[0],
                        snapshot.DnsServers.Count > 1 ? snapshot.DnsServers[1] : null, errors);
                }
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
            if (!SubnetCalculator.TryParseAddress(address, out var parsed))
            {
                return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed,
                    $"IP 地址 \"{address}\" 无法解析", ReadAdapter(adapterId));
            }

            if (prefixLength is < 0 or > 32)
            {
                return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed,
                    $"前缀长度 {prefixLength} 非法（应在 0 ~ 32 之间）", ReadAdapter(adapterId));
            }

            var before = AdapterReadback.TryRead(adapter, _log);
            var target = SubnetCalculator.Format(parsed);
            Log("Information", $"添加辅助 IP（netsh）：{adapter.Name} ← {target}/{prefixLength}");

            var errors = new List<string>();
            Run(errors, "interface", "ipv4", "add", "address", NetshCommand.NameArgument(adapter.Name),
                $"address={target}", $"mask={AdapterReadback.MaskText(prefixLength)}");

            var readback = AdapterReadback.TryRead(adapter, _log);
            if (errors.Count > 0)
            {
                return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed, string.Join("；", errors), readback);
            }

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
                return NetConfigResult.Ok(before, $"网卡上没有地址 {address}，无需移除");
            }

            Log("Information", $"移除辅助 IP（netsh）：{adapter.Name} ← {address}");
            var errors = new List<string>();
            foreach (var target in targets)
            {
                Run(errors, "interface", "ipv4", "delete", "address", NetshCommand.NameArgument(adapter.Name),
                    $"address={target.Address}", $"mask={target.Mask}");
            }

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
    // 内部：结果组装、DNS、netsh 执行
    // -----------------------------------------------------------------------------------------

    private NetConfigResult BuildResult(NetworkConfigRequest request, AdapterSnapshot? before,
        AdapterSnapshot? readback, List<string> errors)
    {
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

        return NetConfigResult.Ok(readback, "已应用：" + (readback?.SummaryText ?? readback?.Id));
    }

    private void ApplyDns(NetworkInterface adapter, string? dns1, string? dns2, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(dns1)) return;

        string name = NetshCommand.NameArgument(adapter.Name);
        // validate=no：跳过 netsh 的 DNS 可达性校验，避免在离线现场长时间阻塞。
        Run(errors, "interface", "ipv4", "set", "dns", name, "static", dns1, "primary", "validate=no");
        if (string.IsNullOrWhiteSpace(dns2)) return;

        Run(errors, "interface", "ipv4", "add", "dns", name, dns2, "index=2", "validate=no");
    }

    private void Run(List<string> errors, params string[] arguments)
    {
        if (NetshCommand.Run(_log, out int exitCode, out var failure, arguments)) return;
        errors.Add(failure ?? $"netsh 返回 {exitCode}");
    }

    private void Log(string level, string message) => _log?.Invoke(level, "NetConfig", message);

    private NetConfigResult AdapterNotFound(string adapterId) => NetConfigResult.Fail(
        ErrorCodes.PlatformAdapterNotFound,
        $"找不到网卡 {adapterId}：可能已被拔出、禁用或重命名，请刷新网卡列表后重试");
}
