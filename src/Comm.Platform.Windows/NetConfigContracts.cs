using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Comm.Core.Errors;

namespace Comm.Platform.Windows;

// ---------------------------------------------------------------------------------------------
// M3a 契约（R-07 快速修改 IP/掩码/网关、R-13 辅助 IP）。
//
// 分层与权限（规划 AD-07 / AD-08 / 12.5）：
//   主程序（普通用户） → 只读枚举 + 校验 + 快照落盘
//                      → INetConfigHelper（命名管道）→ Comm.NetConfigHelper（管理员，改网卡）
//   因此本文件里的写入类 API 都只是"请求"，真正的写操作一律由提权助手执行并回读校验。
//
// 本文件是 Comm.Platform.Windows 与 Comm.NetConfigHelper 之间的**冻结契约**：
// 两端必须同时改动（含 tests/Comm.Platform.Windows.Tests 与 tests/Comm.NetConfigHelper.Tests）。
// ---------------------------------------------------------------------------------------------

/// <summary>一个 IPv4 地址条目（地址 + 前缀长度 + 是否来自 DHCP/自动配置）。</summary>
public sealed record Ipv4AddressInfo(
    string Address,
    int PrefixLength,
    string Mask,
    bool IsDhcpLease = false,
    bool IsAutomatic = false)
{
    public string Cidr => $"{Address}/{PrefixLength}";

    public string DisplayText =>
        $"{Cidr}（掩码 {Mask}{(IsDhcpLease ? " · DHCP" : string.Empty)}{(IsAutomatic ? " · 自动" : string.Empty)}）";
}

/// <summary>
/// 网卡配置快照（规划 15.8 第 1 步）：应用前读取并落盘，失败或倒计时到期时按它回滚。
/// 只包含回滚需要的字段；不包含任何凭据（网卡配置本身没有凭据，见规划 4.10.5）。
/// </summary>
public sealed record AdapterSnapshot
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string MacAddress { get; init; } = string.Empty;
    public bool IsUp { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsLoopback { get; init; }
    public bool DhcpEnabled { get; init; }

    public IReadOnlyList<Ipv4AddressInfo> Ipv4 { get; init; } = Array.Empty<Ipv4AddressInfo>();
    public IReadOnlyList<string> Gateways { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DnsServers { get; init; } = Array.Empty<string>();

    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;

    public string SummaryText =>
        $"{(IsUp ? "已连接" : "未连接")} · {(DhcpEnabled ? "DHCP" : "静态")} · " +
        $"{(Ipv4.Count == 0 ? "无 IPv4" : string.Join(" / ", Ipv4.Select(a => a.Cidr)))} · " +
        $"网关 {(Gateways.Count == 0 ? "-" : string.Join(",", Gateways))}";
}

/// <summary>应用静态地址或切换 DHCP 的请求（规划 4.8.2）。</summary>
public sealed record NetworkConfigRequest
{
    /// <summary>true 表示切换到 DHCP；此时 <see cref="Address"/> 等字段被忽略。</summary>
    public bool DhcpEnabled { get; init; }

    public string? Address { get; init; }
    public int PrefixLength { get; init; } = 24;

    /// <summary>替换网卡上已有的 IPv4 地址（false = 保留原有地址，等价于"辅助 IP"，见 R-13）。</summary>
    public bool ReplaceExistingAddresses { get; init; } = true;

    /// <summary>默认网关；null/空表示不改动网关，<see cref="ClearGateway"/> = true 时清除。</summary>
    public string? Gateway { get; init; }

    public bool ClearGateway { get; init; }

    /// <summary>是否同时改写 DNS（规划 4.8.2：DNS 为可选项，需单独确认）。</summary>
    public bool ApplyDns { get; init; }

    public string? Dns1 { get; init; }
    public string? Dns2 { get; init; }

    /// <summary>安全恢复倒计时秒数（0 = 关闭；由提权助手持有计时，见 D-6、12.5）。</summary>
    public int AutoRollbackSeconds { get; init; }
}

/// <summary>网卡配置操作结果。失败时 <see cref="ErrorCode"/> 取自 <see cref="ErrorCodes"/>（PLT-5xxx / VAL-4009）。</summary>
public sealed record NetConfigResult
{
    private NetConfigResult(bool isSuccess, string? errorCode, string? message, AdapterSnapshot? readback, bool rolledBack)
    {
        IsSuccess = isSuccess;
        ErrorCode = errorCode;
        Message = message;
        Readback = readback;
        RolledBack = rolledBack;
    }

    public bool IsSuccess { get; }
    public string? ErrorCode { get; }
    public string? Message { get; }

    /// <summary>应用后回读得到的实际状态（失败且已回滚时为回滚后的状态；不可读时为 null）。</summary>
    public AdapterSnapshot? Readback { get; }

    /// <summary>失败后是否已经自动回滚到快照。</summary>
    public bool RolledBack { get; }

    public static NetConfigResult Ok(AdapterSnapshot? readback = null, string? message = null, bool rolledBack = false) =>
        new(true, null, message, readback, rolledBack);

    public static NetConfigResult Fail(string errorCode, string message, AdapterSnapshot? readback = null, bool rolledBack = false) =>
        new(false, errorCode, message, readback, rolledBack);

    public string ToUserText() => IsSuccess
        ? Message ?? "已应用"
        : $"[{ErrorCode}] {Message}" + (RolledBack ? "（已自动恢复到修改前的配置）" : string.Empty);
}

/// <summary>
/// 网卡读写后端。生产实现是 IP Helper（<c>IpHelperBackend</c>），降级实现是 netsh（只看退出码，不解析文本，见 AD-07）；
/// 测试实现是内存模拟后端（见 tests/Comm.Platform.Windows.Tests）。
/// </summary>
public interface INetworkAdapterBackend
{
    /// <summary>只读枚举（普通权限即可）。</summary>
    IReadOnlyList<AdapterSnapshot> ReadAdapters();

    /// <summary>读取单个网卡；不存在时返回 null。</summary>
    AdapterSnapshot? ReadAdapter(string adapterId);

    /// <summary>应用静态地址 / 切换 DHCP，并回读校验。需要管理员权限。</summary>
    NetConfigResult Apply(string adapterId, NetworkConfigRequest request);

    /// <summary>按快照回滚（先删除快照中不存在的地址，再补回快照中的地址/网关/DNS/DHCP 状态）。需要管理员权限。</summary>
    NetConfigResult Restore(string adapterId, AdapterSnapshot snapshot);

    /// <summary>叠加一个辅助 IP，保留原有地址（R-13）。需要管理员权限。</summary>
    NetConfigResult AddAuxiliaryAddress(string adapterId, string address, int prefixLength);

    /// <summary>移除一个辅助 IP（仅移除该地址，不动其他地址）。需要管理员权限。</summary>
    NetConfigResult RemoveAuxiliaryAddress(string adapterId, string address);
}

/// <summary>后端工厂：Windows 上返回 IP Helper 实现，失败时降级到 netsh。</summary>
public static class AdapterBackendFactory
{
    /// <summary>创建生产后端。测试请直接使用内存后端，不要调用本方法。</summary>
    public static INetworkAdapterBackend Create() => new IpHelperBackend();
}

// ---------------------------------------------------------------------------------------------
// 提权助手通信契约（规划 12.5）
// ---------------------------------------------------------------------------------------------

/// <summary>助手只接受这几类命令；任何其他字符串都必须被拒绝（禁止执行任意命令行）。</summary>
public static class HelperCommands
{
    /// <summary>读取所有网卡快照。</summary>
    public const string Snapshot = "snapshot";

    /// <summary>应用静态地址 / DHCP。</summary>
    public const string Apply = "apply";

    /// <summary>按快照回滚。</summary>
    public const string Restore = "restore";

    /// <summary>叠加辅助 IP。</summary>
    public const string AddAuxiliary = "add-aux";

    /// <summary>移除辅助 IP。</summary>
    public const string RemoveAuxiliary = "remove-aux";

    /// <summary>确认保留当前配置（取消倒计时，见规划 4.8.3）。</summary>
    public const string Confirm = "confirm";

    /// <summary>查询助手状态（是否还在倒计时、剩余秒数）。</summary>
    public const string Status = "status";

    /// <summary>让助手退出（空闲时也会自动退出）。</summary>
    public const string Shutdown = "shutdown";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Snapshot, Apply, Restore, AddAuxiliary, RemoveAuxiliary, Confirm, Status, Shutdown
    };

    public static bool IsKnown(string? command) =>
        command is not null && All.Contains(command, StringComparer.Ordinal);
}

/// <summary>助手请求。<see cref="Payload"/> 为 JSON（NetworkConfigRequest / AdapterSnapshot 等）。</summary>
public sealed record HelperRequest
{
    public string Command { get; init; } = string.Empty;
    public string? AdapterId { get; init; }
    public string? Payload { get; init; }

    /// <summary>本次操作是否已在主程序侧完成用户确认（审计用；助手的固定命令集才是真正的边界）。</summary>
    public bool Confirmed { get; init; }
}

/// <summary>助手响应。</summary>
public sealed record HelperResponse
{
    public bool IsSuccess { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }

    /// <summary>JSON 负载：快照数组或单个 AdapterSnapshot。</summary>
    public string? Payload { get; init; }

    /// <summary>助手当前是否持有未确认的回滚倒计时。</summary>
    public bool RollbackArmed { get; init; }

    /// <summary>回滚倒计时剩余秒数（未启用时为 0）。</summary>
    public int RollbackSecondsRemaining { get; init; }

    public static HelperResponse Ok(string? payload = null, string? message = null,
        bool rollbackArmed = false, int remainingSeconds = 0) =>
        new() { IsSuccess = true, Payload = payload, Message = message, RollbackArmed = rollbackArmed, RollbackSecondsRemaining = remainingSeconds };

    public static HelperResponse Fail(string errorCode, string message) =>
        new() { IsSuccess = false, ErrorCode = errorCode, Message = message };

    public string ToUserText() => IsSuccess ? Message ?? "成功" : $"[{ErrorCode}] {Message}";
}

/// <summary>
/// 管道线格式：每条消息 = 4 字节小端长度 + UTF-8 JSON。
/// 主程序（<c>NetConfigHelperClient</c>）与助手（<c>Comm.NetConfigHelper</c>）必须都走这里，避免两边各写一套。
/// </summary>
public static class HelperProtocol
{
    public const int MaxMessageBytes = 1024 * 1024;

    public const int ProtocolVersion = 1;

    /// <summary>由主程序生成的一次性管道名（规划 12.5：管道名带随机后缀）。</summary>
    /// 一条连接 = 一条请求 + 一条响应；客户端每次发送新建一次连接，助手循环接受连接。
    public static string CreatePipeName() => $"comm-netcfg-{Guid.NewGuid():N}";

    /// <summary>本次运行使用的完整管道名（助手用 <c>--pipe</c> 参数收到它）。</summary>
    public static string PipeName(string baseName) => baseName;

    public static string Serialize(HelperRequest request) =>
        JsonSerializer.Serialize(request, HelperJson.Options);

    public static HelperRequest? ParseRequest(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<HelperRequest>(json, HelperJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Serialize(HelperResponse response) =>
        JsonSerializer.Serialize(response, HelperJson.Options);

    public static HelperResponse? ParseResponse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<HelperResponse>(json, HelperJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>写入一条带长度前缀的消息。</summary>
    public static void WriteMessage(Stream stream, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > MaxMessageBytes)
        {
            throw new InvalidOperationException($"助手消息过大（{payload.Length} 字节，上限 {MaxMessageBytes}）");
        }

        Span<byte> header = stackalloc byte[4];
        BitConverter.TryWriteBytes(header, payload.Length);
        stream.Write(header);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }

    /// <summary>读取一条带长度前缀的消息；流结束或长度非法时返回 null。</summary>
    public static string? ReadMessage(Stream stream)
    {
        var header = new byte[4];
        if (!ReadExact(stream, header, 4)) return null;

        int length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > MaxMessageBytes) return null;

        var payload = new byte[length];
        return ReadExact(stream, payload, length) ? Encoding.UTF8.GetString(payload) : null;
    }

    private static bool ReadExact(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read <= 0) return false;
            offset += read;
        }

        return true;
    }
}

/// <summary>助手协议的 JSON 选项。</summary>
public static class HelperJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>拉起提权助手的结果。</summary>
public sealed record HelperLaunchResult(Process? Process, bool ElevationDenied, string? Error);

/// <summary>可替换的启动器（测试用假实现验证"UAC 被拒绝"路径，不需要真的弹 UAC）。</summary>
public interface IHelperLauncher
{
    /// <summary>用 runas 动词拉起助手。用户拒绝 UAC 时必须返回 <see cref="HelperLaunchResult.ElevationDenied"/> = true。</summary>
    HelperLaunchResult Launch(string helperPath, string pipeName, int idleExitMinutes);
}

/// <summary>提权助手客户端。主程序只通过它与管理员进程交互（AD-08）。</summary>
public interface INetConfigHelper : IDisposable
{
    /// <summary>助手可执行文件是否存在于预期位置。</summary>
    bool IsHelperPresent { get; }

    /// <summary>预期路径（界面在助手缺失时显示它，便于现场排查）。</summary>
    string ExpectedPath { get; }

    /// <summary>最近一次操作的状态说明（供界面展示）。</summary>
    string StatusText { get; }

    /// <summary>发送一条命令；内部负责按需拉起助手、重试一次管道连接、超时。</summary>
    HelperResponse Send(HelperRequest request, int timeoutMs = 60_000);
}

/// <summary>定位提权助手可执行文件：① 环境变量 <c>COMM_NETCFG_HELPER</c>；② 程序目录；③ 程序目录 <c>helper/</c>。</summary>
public static class NetConfigHelperLocator
{
    public const string EnvironmentVariable = "COMM_NETCFG_HELPER";

    public const string FileName = "Comm.NetConfigHelper.exe";

    public static string Resolve(string? baseDirectory = null)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment)) return fromEnvironment;

        var root = baseDirectory ?? AppContext.BaseDirectory;
        var direct = Path.Combine(root, FileName);
        if (File.Exists(direct)) return direct;

        return Path.Combine(root, "helper", FileName);
    }

    /// <summary>界面与日志用的候选路径说明。</summary>
    public static string DescribeSearchPaths(string? baseDirectory = null)
    {
        var root = baseDirectory ?? AppContext.BaseDirectory;
        return string.Join("；", new[]
        {
            $"{EnvironmentVariable} 环境变量",
            Path.Combine(root, FileName),
            Path.Combine(root, "helper", FileName)
        });
    }
}
