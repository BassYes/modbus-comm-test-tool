using System.Text.Json;
using Comm.Core.Errors;
using Comm.Platform.Windows;

namespace Comm.Platform.Windows.Tests;

/// <summary>
/// 假提权助手（规划 16.3.7）：记录收到的命令、可脚本化响应、可模拟"助手缺失 / UAC 被拒 / 回滚失败"。
///
/// 默认行为是"照做"：收到 apply/add-aux/remove-aux/restore 时直接改 <see cref="FakeAdapterBackend"/>
/// 里的网卡状态，这样服务层的回读校验才能拿到"真实生效后"的读数。
/// 把 <see cref="ApplyMutations"/> 关掉即可模拟"助手说成功但网卡其实没变"（回读不一致 → PLT-5005）。
/// </summary>
public sealed class FakeNetConfigHelper : INetConfigHelper
{
    private readonly FakeAdapterBackend _backend;

    public FakeNetConfigHelper(FakeAdapterBackend backend)
    {
        _backend = backend;
    }

    /// <summary>收到的全部请求（按顺序）。</summary>
    public List<HelperRequest> Requests { get; } = new();

    public bool IsHelperPresent { get; set; } = true;

    public string ExpectedPath { get; set; } = @"C:\app\Comm.NetConfigHelper.exe";

    public string StatusText { get; set; } = "假提权助手就绪";

    /// <summary>是否真的修改网卡（false = 模拟"助手说成功但网卡没变"）。</summary>
    public bool ApplyMutations { get; set; } = true;

    /// <summary>restore 命令是否失败（模拟回滚失败 → PLT-5006）。</summary>
    public bool FailRestore { get; set; }

    /// <summary>指定的命令返回固定响应（用于模拟助手侧错误码）。</summary>
    public Dictionary<string, HelperResponse> ScriptedResponses { get; } = new(StringComparer.Ordinal);

    /// <summary>完全自定义处理逻辑（优先级最高）。</summary>
    public Func<HelperRequest, HelperResponse?>? OnSend { get; set; }

    public bool Disposed { get; private set; }

    /// <summary>某个命令收到的请求。</summary>
    public IReadOnlyList<HelperRequest> RequestsOf(string command) =>
        Requests.Where(r => r.Command == command).ToArray();

    public HelperResponse Send(HelperRequest request, int timeoutMs = 60_000)
    {
        Requests.Add(request);

        if (!HelperCommands.IsKnown(request.Command))
        {
            return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol, "未知命令");
        }

        if (OnSend is not null)
        {
            var custom = OnSend(request);
            if (custom is not null) return custom;
        }

        if (ScriptedResponses.TryGetValue(request.Command, out var scripted)) return scripted;

        switch (request.Command)
        {
            case HelperCommands.Apply:
                return HandleApply(request);

            case HelperCommands.AddAuxiliary:
                return HandleAddAuxiliary(request);

            case HelperCommands.RemoveAuxiliary:
                return HandleRemoveAuxiliary(request);

            case HelperCommands.Restore:
                return HandleRestore(request);

            case HelperCommands.Confirm:
                return HelperResponse.Ok(message: "已保留当前配置");

            case HelperCommands.Status:
                return HelperResponse.Ok(message: "助手空闲");

            case HelperCommands.Shutdown:
                return HelperResponse.Ok(message: "助手退出");

            default:
                return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol, $"未实现的命令 {request.Command}");
        }
    }

    public void Dispose() => Disposed = true;

    // -----------------------------------------------------------------------------------------
    // 命令处理：把请求真的"写"到假网卡上
    // -----------------------------------------------------------------------------------------

    private HelperResponse HandleApply(HelperRequest request)
    {
        if (!ApplyMutations) return HelperResponse.Ok(message: "（模拟）已应用，但没有真正改动");

        if (request.AdapterId is null || _backend.ReadAdapter(request.AdapterId) is not { } current)
        {
            return HelperResponse.Fail(ErrorCodes.PlatformAdapterNotFound, "找不到网卡");
        }

        var config = Parse<NetworkConfigRequest>(request.Payload);
        if (config is null) return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol, "apply 负载无法解析");

        _backend.Update(ApplyRequest(current, config));
        return HelperResponse.Ok(message: "已应用配置",
            rollbackArmed: config.AutoRollbackSeconds > 0,
            remainingSeconds: config.AutoRollbackSeconds);
    }

    private HelperResponse HandleAddAuxiliary(HelperRequest request)
    {
        if (!ApplyMutations) return HelperResponse.Ok(message: "（模拟）已添加，但没有真正改动");
        if (request.AdapterId is null || _backend.ReadAdapter(request.AdapterId) is not { } current)
        {
            return HelperResponse.Fail(ErrorCodes.PlatformAdapterNotFound, "找不到网卡");
        }

        var config = Parse<NetworkConfigRequest>(request.Payload);
        if (config?.Address is null) return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol, "add-aux 负载无法解析");

        if (current.Ipv4.Any(a => a.Address == config.Address))
        {
            return HelperResponse.Ok(message: "地址已存在");
        }

        _backend.Update(current with
        {
            Ipv4 = current.Ipv4
                .Append(FakeAdapterBackend.Address(config.Address, config.PrefixLength))
                .ToArray()
        });
        return HelperResponse.Ok(message: "已添加辅助 IP");
    }

    private HelperResponse HandleRemoveAuxiliary(HelperRequest request)
    {
        if (!ApplyMutations) return HelperResponse.Ok(message: "（模拟）已移除，但没有真正改动");
        if (request.AdapterId is null || _backend.ReadAdapter(request.AdapterId) is not { } current)
        {
            return HelperResponse.Fail(ErrorCodes.PlatformAdapterNotFound, "找不到网卡");
        }

        var config = Parse<NetworkConfigRequest>(request.Payload);
        if (config?.Address is null) return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol, "remove-aux 负载无法解析");

        _backend.Update(current with
        {
            Ipv4 = current.Ipv4.Where(a => a.Address != config.Address).ToArray()
        });
        return HelperResponse.Ok(message: "已移除地址");
    }

    private HelperResponse HandleRestore(HelperRequest request)
    {
        if (FailRestore)
        {
            return HelperResponse.Fail(ErrorCodes.PlatformNetConfigRollbackFailed, "（模拟）回滚失败");
        }

        if (request.AdapterId is null || _backend.ReadAdapter(request.AdapterId) is not { } current)
        {
            return HelperResponse.Fail(ErrorCodes.PlatformAdapterNotFound, "找不到网卡");
        }

        var snapshot = Parse<AdapterSnapshot>(request.Payload);
        if (snapshot is null) return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol, "restore 负载无法解析");

        // 只把"会被改坏"的字段还原回去，Id/Name 等保持当前值。
        _backend.Update(current with
        {
            DhcpEnabled = snapshot.DhcpEnabled,
            Ipv4 = snapshot.Ipv4,
            Gateways = snapshot.Gateways,
            DnsServers = snapshot.DnsServers
        });
        return HelperResponse.Ok(message: "已回滚");
    }

    private static AdapterSnapshot ApplyRequest(AdapterSnapshot source, NetworkConfigRequest request)
    {
        if (request.DhcpEnabled)
        {
            // 切到 DHCP：地址交由 DHCP 客户端重新分配，这里保留地址以简化断言。
            return source with { DhcpEnabled = true };
        }

        var target = FakeAdapterBackend.Address(request.Address!, request.PrefixLength);
        IReadOnlyList<Ipv4AddressInfo> addresses = request.ReplaceExistingAddresses
            ? new[] { target }
            : source.Ipv4
                .Where(a => a.Address != target.Address)
                .Append(target)
                .ToArray();

        IReadOnlyList<string> gateways = request.ClearGateway
            ? Array.Empty<string>()
            : string.IsNullOrWhiteSpace(request.Gateway)
                ? source.Gateways
                : new[] { request.Gateway! };

        IReadOnlyList<string> dns = request.ApplyDns && !string.IsNullOrWhiteSpace(request.Dns1)
            ? new[] { request.Dns1!, request.Dns2 ?? string.Empty }.Where(d => !string.IsNullOrWhiteSpace(d)).ToArray()
            : source.DnsServers;

        return source with { DhcpEnabled = false, Ipv4 = addresses, Gateways = gateways, DnsServers = dns };
    }

    private static T? Parse<T>(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(payload, HelperJson.Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}

/// <summary>假启动器：记录拉起次数，可脚本化"用户取消 UAC"（不需要真的弹窗）。</summary>
public sealed class FakeHelperLauncher : IHelperLauncher
{
    /// <summary>下一次拉起的返回值；null 表示"提权成功但进程不存在"。</summary>
    public HelperLaunchResult Result { get; set; } = new(null, false, "（模拟）未启动进程");

    public int LaunchCount { get; private set; }

    public string? LastHelperPath { get; private set; }

    public string? LastPipeName { get; private set; }

    public int LastIdleExitMinutes { get; private set; }

    public HelperLaunchResult Launch(string helperPath, string pipeName, int idleExitMinutes)
    {
        LaunchCount++;
        LastHelperPath = helperPath;
        LastPipeName = pipeName;
        LastIdleExitMinutes = idleExitMinutes;
        return Result;
    }
}
