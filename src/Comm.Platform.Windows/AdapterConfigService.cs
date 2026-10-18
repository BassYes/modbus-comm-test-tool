using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Comm.Core;
using Comm.Core.Errors;
using Comm.Network;

namespace Comm.Platform.Windows;

/// <summary>
/// 一份落盘的网卡配置快照（规划 15.8 第 1 步）。
///
/// 状态位（规划 15.8 第 5、6 步）：
///   <see cref="RollbackArmed"/> = 提权助手仍持有未确认的安全恢复倒计时；
///   <see cref="Kept"/>          = 用户已确认保留（或本来就没有启用倒计时）。
/// 二者组合为「已布防、未保留」时，就是程序下次启动要提示恢复的未决快照。
/// </summary>
public sealed record AdapterSnapshotFile(
    string Path,
    string AdapterId,
    AdapterSnapshot Snapshot,
    DateTimeOffset CreatedAt,
    string Operation,
    string RequestSummary,
    bool Kept,
    bool RollbackArmed,
    DateTimeOffset? RollbackDeadline)
{
    /// <summary>快照文件名（界面列表显示用）。</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>是否处于"等待用户确认保留"的未决状态（规划 15.8 第 6 步）。</summary>
    public bool IsPending => RollbackArmed && !Kept;
}

/// <summary>
/// 网卡配置服务：界面（WPF）访问网卡配置的唯一入口（规划 14.6、15.8、R-07 / R-13）。
///
/// 职责与顺序（严格按规划 15.8）：
///   第 1 步 读取并在**修改前**原子落盘快照（&lt;snapshotDirectory&gt;/netsnap/）；
///   第 2 步 <see cref="NetworkConfigValidator"/> 校验——失败直接返回 VAL-4009，不落快照、不下发；
///   第 3 步 通过 <see cref="INetConfigHelper"/> 交给管理员助手执行，应用后回读校验；
///   第 4 步 回读不一致（PLT-5005）或助手失败时按快照回滚；
///   第 5 步 <see cref="ConfirmKeep"/> 确认保留并取消倒计时；
///   第 6 步 <see cref="FindPendingSnapshot"/> 供启动时检测"上次异常退出留下的未决快照"。
///
/// 权限边界（AD-08）：本类**只读** <see cref="INetworkAdapterBackend"/>，任何写入都必须经过提权助手。
/// 所有 public 方法都不抛异常（规划 15.1），预期失败一律用 <see cref="NetConfigResult"/> 表达。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AdapterConfigService
{
    /// <summary>快照最多保留的份数（超出后按时间倒序删除最旧的，规划 15.10）。</summary>
    public const int MaxSnapshotFiles = 20;

    /// <summary>快照子目录名（相对构造参数 <c>snapshotDirectory</c>）。</summary>
    public const string SnapshotFolderName = "netsnap";

    private readonly INetworkAdapterBackend _backend;
    private readonly INetConfigHelper _helper;
    private readonly NetworkConfigValidationOptions _options;
    private readonly Action<string, string, string?>? _log;
    private readonly string _snapshotDirectory;

    /// <summary>创建服务。</summary>
    /// <param name="backend">只读后端（生产为 <see cref="IpHelperBackend"/>，测试为内存后端）。</param>
    /// <param name="helper">提权助手客户端（唯一的写入通道）。</param>
    /// <param name="snapshotDirectory">快照根目录；实际写入其下的 <c>netsnap/</c>。</param>
    /// <param name="options">校验选项（默认 = 规划 15.8 的硬性检查）。</param>
    /// <param name="log">可选日志回调（级别、分类、正文）；库内不使用 Console。</param>
    public AdapterConfigService(
        INetworkAdapterBackend backend,
        INetConfigHelper helper,
        string snapshotDirectory,
        NetworkConfigValidationOptions? options = null,
        Action<string, string, string?>? log = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(helper);
        if (string.IsNullOrWhiteSpace(snapshotDirectory))
        {
            throw new ArgumentException("必须指定快照根目录", nameof(snapshotDirectory));
        }

        _backend = backend;
        _helper = helper;
        _options = options ?? new NetworkConfigValidationOptions();
        _log = log;
        _snapshotDirectory = System.IO.Path.Combine(snapshotDirectory, SnapshotFolderName);
    }

    /// <summary>快照目录的完整路径（界面"打开快照目录"用）。</summary>
    public string SnapshotDirectory => _snapshotDirectory;

    /// <summary>最近一次校验结果的完整内容（界面显示 <c>SummaryText</c> / <c>Warning</c> 用）；未校验时为 null。</summary>
    public NetworkConfigValidation? LastValidation { get; private set; }

    /// <summary>最近一次与助手交互的响应（界面显示倒计时剩余秒数等状态用）。</summary>
    public HelperResponse? LastHelperStatus { get; private set; }

    /// <summary>助手状态说明：优先最近一次响应，否则用客户端的握手状态。</summary>
    public string LastHelperStatusText => LastHelperStatus?.ToUserText() ?? _helper.StatusText;

    // -----------------------------------------------------------------------------------------
    // 只读（普通权限）
    // -----------------------------------------------------------------------------------------

    /// <summary>枚举网卡（规划 4.8.1：普通权限即可读取，绝不改配置）。</summary>
    public IReadOnlyList<AdapterSnapshot> ReadAdapters()
    {
        try
        {
            var adapters = _backend.ReadAdapters();
            Log("Information", $"读取到 {adapters.Count} 块网卡");
            return adapters;
        }
        catch (Exception ex)
        {
            Log("Error", $"枚举网卡异常：{ex.Message}");
            return Array.Empty<AdapterSnapshot>();
        }
    }

    /// <summary>读取单块网卡；不存在返回 null。</summary>
    public AdapterSnapshot? FindAdapter(string adapterId)
    {
        try
        {
            return string.IsNullOrWhiteSpace(adapterId) ? null : _backend.ReadAdapter(adapterId);
        }
        catch (Exception ex)
        {
            Log("Error", $"读取网卡 {adapterId} 异常：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把用户输入换算成校验结果（界面在点"应用"前实时调用；失败不产生任何副作用）。
    /// 供界面预览 <see cref="NetworkConfigValidation.SummaryText"/> / <see cref="NetworkConfigValidation.Warning"/>。
    /// </summary>
    public Result<NetworkConfigValidation> Validate(string adapterId, NetworkConfigRequest request)
    {
        var adapter = FindAdapter(adapterId);
        if (adapter is null)
        {
            return Result<NetworkConfigValidation>.Fail(AdapterNotFoundError(adapterId));
        }

        var result = NetworkConfigValidator.Validate(request, adapter, _options);
        LastValidation = result.IsSuccess ? result.Value : null;
        return result;
    }

    // -----------------------------------------------------------------------------------------
    // 写操作：校验 → 助手可用性 → 快照 → 下发 → 回读
    // -----------------------------------------------------------------------------------------

    /// <summary>应用静态地址 / 网关 / DNS（R-07，规划 4.8.2）。</summary>
    public NetConfigResult ApplyStatic(string adapterId, NetworkConfigRequest request)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);

            var adapter = FindAdapter(adapterId);
            if (adapter is null) return AdapterNotFound(adapterId);

            // 第 2 步：校验。失败不落快照、不下发（规划 15.8）。
            var validation = NetworkConfigValidator.Validate(request, adapter, _options);
            if (!validation.IsSuccess)
            {
                LastValidation = null;
                Log("Warning", $"校验未通过：{validation.Error?.Message}");
                return ValidationFailed(validation.Error);
            }

            LastValidation = validation.Value;
            var value = validation.Value!;

            if (value.IsNoOp)
            {
                // 幂等：配置本来就是目标状态，不发命令、不落快照（避免产生无意义的未决快照）。
                return NetConfigResult.Ok(adapter, value.Warning ?? "当前配置与目标一致，未做修改");
            }

            if (!_helper.IsHelperPresent) return HelperUnavailable();

            // 第 1 步：先落快照，再改网卡——没有快照就不动网卡。
            bool rollbackArmed = request.AutoRollbackSeconds > 0;
            string summary = request.DhcpEnabled
                ? "切换到 DHCP"
                : value.SummaryText;

            var snapshotFile = WriteSnapshot(adapter, HelperCommands.Apply, summary, rollbackArmed,
                request.AutoRollbackSeconds);
            if (snapshotFile is null) return SnapshotFailed(adapterId);

            Log("Information", $"应用配置：{adapter.Name} ← {(request.DhcpEnabled ? "DHCP" : value.SummaryText)}");
            var response = Send(HelperCommands.Apply, adapter.Id, request);

            if (!response.IsSuccess)
            {
                // UAC 被拒 / 助手连接失败：网卡没有被改动过，快照不能残留（否则下次启动误报未决回滚）。
                if (response.ErrorCode == ErrorCodes.PlatformHelperUnavailable)
                {
                    DeleteSnapshotFile(snapshotFile);
                    return NetConfigResult.Fail(ErrorCodes.PlatformHelperUnavailable,
                        response.Message ?? _helper.StatusText, Readback(adapterId));
                }

                // 第 4 步：助手失败也要回到修改前，避免网卡停在半新半旧的状态。
                bool rolledBack = TryRollback(snapshotFile);
                return NetConfigResult.Fail(response.ErrorCode ?? ErrorCodes.PlatformNetConfigApplyFailed,
                    response.Message ?? "提权助手未能应用配置", Readback(adapterId), rolledBack);
            }

            // 第 3 步：回读校验——助手说成功不算数，网卡上真变了才算。
            var readback = Readback(adapterId);
            var diff = NetConfigCompare.ForApply(request, adapter, readback);
            if (diff is not null)
            {
                Log("Warning", $"回读不一致：{diff}");
                bool rolledBack = TryRollback(snapshotFile);
                return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigReadbackMismatch, diff,
                    Readback(adapterId), rolledBack);
            }

            // 未启用倒计时 = 用户此刻就在现场，视为已确认（规划 4.8.3：倒计时默认关闭）。
            if (!rollbackArmed)
            {
                MarkSnapshot(snapshotFile, rollbackArmed: false, kept: true);
            }

            string message = request.DhcpEnabled ? "已切换到 DHCP" : $"已应用 {value.SummaryText}";
            if (rollbackArmed)
            {
                int remaining = response.RollbackSecondsRemaining > 0
                    ? response.RollbackSecondsRemaining
                    : request.AutoRollbackSeconds;
                message += $"；请在 {remaining} 秒内点击“保留”，否则将自动恢复到修改前的配置";
            }

            if (!string.IsNullOrWhiteSpace(value.Warning)) message += "；" + value.Warning;

            return NetConfigResult.Ok(readback, message);
        }
        catch (Exception ex)
        {
            Log("Error", $"应用网卡配置异常：{ex}");
            return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed,
                $"应用网卡配置时发生异常：{ex.Message}", Readback(adapterId));
        }
    }

    /// <summary>切换为 DHCP（规划 4.8.2），<paramref name="autoRollbackSeconds"/> = 安全恢复倒计时秒数（0 = 关闭）。</summary>
    public NetConfigResult SetDhcp(string adapterId, int autoRollbackSeconds)
    {
        return ApplyStatic(adapterId, new NetworkConfigRequest
        {
            DhcpEnabled = true,
            AutoRollbackSeconds = autoRollbackSeconds < 0 ? 0 : autoRollbackSeconds
        });
    }

    /// <summary>叠加辅助 IP（R-13）：只加地址，不动原有地址、网关与 DNS。</summary>
    public NetConfigResult AddAuxiliaryAddress(string adapterId, string address, int prefixLength)
    {
        try
        {
            var adapter = FindAdapter(adapterId);
            if (adapter is null) return AdapterNotFound(adapterId);

            var validation = NetworkConfigValidator.ValidateAuxiliaryAddress(address, prefixLength, adapter, _options);
            if (!validation.IsSuccess)
            {
                LastValidation = null;
                return ValidationFailed(validation.Error);
            }

            LastValidation = validation.Value;
            var value = validation.Value!;

            if (value.IsNoOp)
            {
                // 幂等：同地址同前缀已经在网卡上，直接成功，不动网卡（R-13）。
                return NetConfigResult.Ok(adapter, value.Warning ?? $"地址 {value.Address}/{value.PrefixLength} 已存在");
            }

            if (!_helper.IsHelperPresent) return HelperUnavailable();

            var snapshotFile = WriteSnapshot(adapter, HelperCommands.AddAuxiliary,
                $"+{value.Address}/{value.PrefixLength}", rollbackArmed: false, autoRollbackSeconds: 0);
            if (snapshotFile is null) return SnapshotFailed(adapterId);

            var response = Send(HelperCommands.AddAuxiliary, adapter.Id, new
            {
                address = value.Address,
                prefixLength = value.PrefixLength
            });

            if (!response.IsSuccess)
            {
                if (response.ErrorCode == ErrorCodes.PlatformHelperUnavailable)
                {
                    DeleteSnapshotFile(snapshotFile);
                    return NetConfigResult.Fail(ErrorCodes.PlatformHelperUnavailable,
                        response.Message ?? _helper.StatusText, Readback(adapterId));
                }

                bool rolledBack = TryRollback(snapshotFile);
                return NetConfigResult.Fail(response.ErrorCode ?? ErrorCodes.PlatformNetConfigApplyFailed,
                    response.Message ?? "提权助手未能添加辅助 IP", Readback(adapterId), rolledBack);
            }

            var readback = Readback(adapterId);
            var expected = AdapterReadback.CidrSet(adapter.Ipv4).Append($"{value.Address}/{value.PrefixLength}");
            var diff = NetConfigCompare.ForAddressSet(expected, readback, "添加辅助 IP");
            if (diff is not null)
            {
                bool rolledBack = TryRollback(snapshotFile);
                return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigReadbackMismatch, diff,
                    Readback(adapterId), rolledBack);
            }

            MarkSnapshot(snapshotFile, rollbackArmed: false, kept: true);
            return NetConfigResult.Ok(readback,
                $"已添加辅助 IP {value.Address}/{value.PrefixLength}（原有地址、网关与 DNS 保持不变）");
        }
        catch (Exception ex)
        {
            Log("Error", $"添加辅助 IP 异常：{ex}");
            return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed,
                $"添加辅助 IP 时发生异常：{ex.Message}", Readback(adapterId));
        }
    }

    /// <summary>移除一个辅助 IP（只移除该地址，不动其他地址；地址不存在时视为已完成）。</summary>
    public NetConfigResult RemoveAuxiliaryAddress(string adapterId, string address)
    {
        try
        {
            var adapter = FindAdapter(adapterId);
            if (adapter is null) return AdapterNotFound(adapterId);

            if (!SubnetCalculator.TryParseAddress(address, out var parsed))
            {
                return NetConfigResult.Fail(ErrorCodes.ValidationNetworkConfig,
                    $"IP 地址格式非法：\"{address}\"", adapter);
            }

            var target = SubnetCalculator.Format(parsed);
            var existing = adapter.Ipv4
                .Where(a => string.Equals(a.Address, target, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (existing.Length == 0)
            {
                return NetConfigResult.Ok(adapter, $"网卡上没有地址 {target}，无需移除");
            }

            if (!_helper.IsHelperPresent) return HelperUnavailable();

            var snapshotFile = WriteSnapshot(adapter, HelperCommands.RemoveAuxiliary,
                $"-{target}", rollbackArmed: false, autoRollbackSeconds: 0);
            if (snapshotFile is null) return SnapshotFailed(adapterId);

            var response = Send(HelperCommands.RemoveAuxiliary, adapter.Id, new
            {
                address = target,
                prefixLength = existing[0].PrefixLength
            });

            if (!response.IsSuccess)
            {
                if (response.ErrorCode == ErrorCodes.PlatformHelperUnavailable)
                {
                    DeleteSnapshotFile(snapshotFile);
                    return NetConfigResult.Fail(ErrorCodes.PlatformHelperUnavailable,
                        response.Message ?? _helper.StatusText, Readback(adapterId));
                }

                bool rolledBack = TryRollback(snapshotFile);
                return NetConfigResult.Fail(response.ErrorCode ?? ErrorCodes.PlatformNetConfigApplyFailed,
                    response.Message ?? $"提权助手未能移除地址 {target}", Readback(adapterId), rolledBack);
            }

            var readback = Readback(adapterId);
            var expected = AdapterReadback.CidrSet(adapter.Ipv4)
                .Where(cidr => !cidr.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase));
            var diff = NetConfigCompare.ForAddressSet(expected, readback, "移除辅助 IP");
            if (diff is not null)
            {
                bool rolledBack = TryRollback(snapshotFile);
                return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigReadbackMismatch, diff,
                    Readback(adapterId), rolledBack);
            }

            MarkSnapshot(snapshotFile, rollbackArmed: false, kept: true);
            return NetConfigResult.Ok(readback, $"已移除地址 {target}（其他地址不受影响）");
        }
        catch (Exception ex)
        {
            Log("Error", $"移除辅助 IP 异常：{ex}");
            return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed,
                $"移除辅助 IP 时发生异常：{ex.Message}", Readback(adapterId));
        }
    }

    /// <summary>
    /// 按指定快照回滚（规划 15.8 第 4、6 步）：启动时用 <see cref="FindPendingSnapshot"/> 取到未决快照后交给本方法。
    /// 失败返回 PLT-5006，提示用户手工恢复。
    /// </summary>
    public NetConfigResult Restore(AdapterSnapshotFile file)
    {
        try
        {
            if (file is null)
            {
                return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigRollbackFailed,
                    "没有指定要恢复的快照文件，无法回滚");
            }

            var adapter = FindAdapter(file.AdapterId) ?? FindAdapter(file.Snapshot.Id);
            if (adapter is null) return AdapterNotFound(file.AdapterId);

            if (!_helper.IsHelperPresent) return HelperUnavailable();

            Log("Information", $"回滚网卡 {adapter.Name} 到快照 {file.FileName}（{file.RequestSummary}）");
            var response = Send(HelperCommands.Restore, adapter.Id, file.Snapshot);
            if (!response.IsSuccess)
            {
                return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigRollbackFailed,
                    response.Message ?? "提权助手回滚失败，请手工核对网卡配置", Readback(adapter.Id));
            }

            var readback = Readback(adapter.Id);
            var diff = NetConfigCompare.ForRestore(file.Snapshot, readback);
            if (diff is not null)
            {
                Log("Error", $"回滚后回读不一致：{diff}");
                return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigReadbackMismatch,
                    diff + "。请手工核对网卡配置（规划 15.8 第 4 步）", readback);
            }

            // 回滚完成 = 配置已回到快照，未决状态随之解除（否则启动检测会重复提示）。
            MarkSnapshot(file, rollbackArmed: false, kept: true);
            return NetConfigResult.Ok(readback, $"已恢复到 {file.Snapshot.SummaryText}");
        }
        catch (Exception ex)
        {
            Log("Error", $"回滚异常：{ex}");
            return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigRollbackFailed,
                $"回滚时发生异常：{ex.Message}，请手工核对网卡配置", Readback(file?.AdapterId ?? string.Empty));
        }
    }

    /// <summary>
    /// 确认保留当前配置（取消助手持有的回滚倒计时，规划 4.8.3 / 15.8 第 5 步）。
    /// 成功后把所有"已布防、未保留"的快照标记为已保留。
    /// </summary>
    public NetConfigResult ConfirmKeep()
    {
        try
        {
            var pending = FindPendingSnapshot();
            if (pending is null)
            {
                return NetConfigResult.Ok(message: "当前没有待确认的回滚倒计时，配置已是保留状态");
            }

            if (!_helper.IsHelperPresent) return HelperUnavailable();

            var response = Send(HelperCommands.Confirm, pending.AdapterId, payload: null);
            if (!response.IsSuccess)
            {
                return NetConfigResult.Fail(response.ErrorCode ?? ErrorCodes.PlatformNetConfigApplyFailed,
                    response.Message ?? "提权助手未确认保留配置", Readback(pending.AdapterId));
            }

            foreach (var armed in ListSnapshots().Where(f => f.IsPending))
            {
                MarkSnapshot(armed, rollbackArmed: false, kept: true);
            }

            return NetConfigResult.Ok(Readback(pending.AdapterId), "已保留当前配置，回滚倒计时已取消");
        }
        catch (Exception ex)
        {
            Log("Error", $"确认保留异常：{ex}");
            return NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed,
                $"确认保留时发生异常：{ex.Message}");
        }
    }

    /// <summary>向助手查询状态（是否还在倒计时、剩余秒数）。</summary>
    public HelperResponse QueryHelperStatus(string? adapterId = null)
    {
        var response = _helper.Send(new HelperRequest { Command = HelperCommands.Status, AdapterId = adapterId });
        LastHelperStatus = response;
        return response;
    }

    // -----------------------------------------------------------------------------------------
    // 快照文件
    // -----------------------------------------------------------------------------------------

    /// <summary>列出已落盘的快照（按写入时间倒序，最多 <see cref="MaxSnapshotFiles"/> 份）。</summary>
    /// <param name="adapterId">只列出指定网卡的快照；null 表示全部。</param>
    public IReadOnlyList<AdapterSnapshotFile> ListSnapshots(string? adapterId = null)
    {
        try
        {
            if (!Directory.Exists(_snapshotDirectory)) return Array.Empty<AdapterSnapshotFile>();

            var files = new List<AdapterSnapshotFile>();
            foreach (var path in Directory.EnumerateFiles(_snapshotDirectory, "*.json"))
            {
                var file = ReadSnapshotFile(path);
                if (file is null) continue;
                if (!string.IsNullOrWhiteSpace(adapterId) &&
                    !string.Equals(file.AdapterId, adapterId, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(file.Snapshot.Id, adapterId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                files.Add(file);
            }

            return files
                .OrderByDescending(f => f.CreatedAt)
                .ThenByDescending(f => f.FileName, StringComparer.Ordinal)
                .Take(MaxSnapshotFiles)
                .ToArray();
        }
        catch (Exception ex)
        {
            Log("Warning", $"列出快照失败：{ex.Message}");
            return Array.Empty<AdapterSnapshotFile>();
        }
    }

    /// <summary>
    /// 查找"未决"快照（规划 15.8 第 6 步）：已布防回滚倒计时、但用户还没确认保留的最新一份。
    /// 程序启动时若返回非 null，说明上次退出前配置可能未被确认，应提示用户恢复。
    /// </summary>
    public AdapterSnapshotFile? FindPendingSnapshot() =>
        ListSnapshots().FirstOrDefault(f => f.RollbackArmed && !f.Kept);

    // -----------------------------------------------------------------------------------------
    // 内部：快照写入 / 状态更新
    // -----------------------------------------------------------------------------------------

    /// <summary>把快照写入临时文件再重命名（规划 15.10 / 9.3 的原子写），失败返回 null。</summary>
    private AdapterSnapshotFile? WriteSnapshot(AdapterSnapshot snapshot, string operation, string requestSummary,
        bool rollbackArmed, int autoRollbackSeconds)
    {
        try
        {
            Directory.CreateDirectory(_snapshotDirectory);

            var createdAt = DateTimeOffset.Now;
            var baseName = $"{ShortId(snapshot.Id)}-{createdAt.LocalDateTime:yyyyMMdd-HHmmss}";
            var path = UniquePath(baseName);

            var file = new AdapterSnapshotFile(
                Path: path,
                AdapterId: snapshot.Id,
                Snapshot: snapshot,
                CreatedAt: createdAt,
                Operation: operation,
                RequestSummary: requestSummary,
                Kept: false,
                RollbackArmed: rollbackArmed,
                RollbackDeadline: rollbackArmed && autoRollbackSeconds > 0
                    ? createdAt.AddSeconds(autoRollbackSeconds)
                    : null);

            WriteFileAtomic(file);
            PruneSnapshots();
            Log("Information", $"已保存配置快照 {file.FileName}（{operation}：{requestSummary}）");
            return file;
        }
        catch (Exception ex)
        {
            Log("Error", $"写入配置快照失败：{ex.Message}");
            return null;
        }
    }

    private void MarkSnapshot(AdapterSnapshotFile file, bool rollbackArmed, bool kept)
    {
        try
        {
            WriteFileAtomic(file with { RollbackArmed = rollbackArmed, Kept = kept });
        }
        catch (Exception ex)
        {
            // 状态位写不进去不影响网卡本身；只是下次启动可能仍提示"未决回滚"。
            Log("Warning", $"更新快照状态失败（{file.FileName}）：{ex.Message}");
        }
    }

    /// <summary>临时文件 + <see cref="File.Move(string,string,bool)"/> 覆盖：要么是完整的新内容，要么是旧内容。</summary>
    private void WriteFileAtomic(AdapterSnapshotFile file)
    {
        var dto = new SnapshotFileDto(
            file.AdapterId,
            file.Snapshot,
            file.CreatedAt,
            file.Operation,
            file.RequestSummary,
            file.Kept,
            file.RollbackArmed,
            file.RollbackDeadline);

        var json = JsonSerializer.Serialize(dto, HelperJson.Options);
        var temporary = file.Path + ".tmp";
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, file.Path, overwrite: true);
        }
        catch
        {
            // 失败时不留下半成品（规划 15.10）。
            TryDelete(temporary);
            throw;
        }
    }

    private AdapterSnapshotFile? ReadSnapshotFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<SnapshotFileDto>(json, HelperJson.Options);
            if (dto?.Snapshot is null) return null;

            var createdAt = dto.CreatedAt == default
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)
                : dto.CreatedAt;

            return new AdapterSnapshotFile(
                Path: path,
                AdapterId: string.IsNullOrWhiteSpace(dto.AdapterId) ? dto.Snapshot.Id : dto.AdapterId,
                Snapshot: dto.Snapshot,
                CreatedAt: createdAt,
                Operation: dto.Operation ?? string.Empty,
                RequestSummary: dto.RequestSummary ?? string.Empty,
                Kept: dto.Kept,
                RollbackArmed: dto.RollbackArmed,
                RollbackDeadline: dto.RollbackDeadline);
        }
        catch (Exception ex)
        {
            // 损坏的快照不能影响整个列表（规划 15.10：损坏文件不覆盖、不阻塞启动）。
            Log("Warning", $"快照文件无法解析（{System.IO.Path.GetFileName(path)}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>只保留最新的 <see cref="MaxSnapshotFiles"/> 份快照。</summary>
    private void PruneSnapshots()
    {
        try
        {
            var files = Directory.EnumerateFiles(_snapshotDirectory, "*.json").ToArray();
            if (files.Length <= MaxSnapshotFiles) return;

            var stale = files
                .Select(path => (Path: path, Time: File.GetLastWriteTimeUtc(path)))
                .OrderByDescending(item => item.Time)
                .ThenByDescending(item => item.Path, StringComparer.Ordinal)
                .Skip(MaxSnapshotFiles);

            foreach (var item in stale)
            {
                TryDelete(item.Path);
                Log("Information", $"已清理旧快照 {System.IO.Path.GetFileName(item.Path)}");
            }
        }
        catch (Exception ex)
        {
            Log("Warning", $"清理旧快照失败：{ex.Message}");
        }
    }

    /// <summary>删除一份刚写入但确定没用上的快照（助手根本没被拉起时）。</summary>
    private void DeleteSnapshotFile(AdapterSnapshotFile file)
    {
        try
        {
            TryDelete(file.Path);
            Log("Warning", $"助手不可用，已删除未使用的快照 {file.FileName}");
        }
        catch (Exception ex)
        {
            Log("Warning", $"删除快照失败（{file.FileName}）：{ex.Message}");
        }
    }

    private string UniquePath(string baseName)
    {
        var path = System.IO.Path.Combine(_snapshotDirectory, baseName + ".json");
        for (int index = 1; index < 100 && File.Exists(path); index++)
        {
            path = System.IO.Path.Combine(_snapshotDirectory, $"{baseName}-{index}.json");
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 删不掉就留着，不影响主流程。
        }
    }

    /// <summary>文件名前缀：取网卡 Id 的前 8 位字母数字（GUID 带大括号，先清洗掉非法文件名字符）。</summary>
    private static string ShortId(string adapterId)
    {
        var cleaned = new string((adapterId ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
        if (cleaned.Length == 0) return "adapter";
        return (cleaned.Length >= 8 ? cleaned[..8] : cleaned).ToLowerInvariant();
    }

    // -----------------------------------------------------------------------------------------
    // 内部：与助手通信
    // -----------------------------------------------------------------------------------------

    /// <summary>下发一条命令并记录最近一次响应；<paramref name="payload"/> 会按 <see cref="HelperJson"/> 序列化。</summary>
    private HelperResponse Send(string command, string adapterId, object? payload)
    {
        var request = new HelperRequest
        {
            Command = command,
            AdapterId = adapterId,
            Payload = payload is null ? null : JsonSerializer.Serialize(payload, HelperJson.Options),
            // 主程序侧已完成校验与用户确认（助手真正的边界是固定命令集，见规划 12.5）。
            Confirmed = true
        };

        HelperResponse response;
        try
        {
            response = _helper.Send(request);
        }
        catch (Exception ex)
        {
            Log("Error", $"与提权助手通信异常：{ex.Message}");
            response = HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol, $"与提权助手通信失败：{ex.Message}");
        }

        LastHelperStatus = response;
        Log(response.IsSuccess ? "Information" : "Warning", $"{command} → {response.ToUserText()}");
        return response;
    }

    /// <summary>尽力回滚到快照；返回是否成功（失败不再抛异常，由调用方转成 PLT-5006 文案）。</summary>
    private bool TryRollback(AdapterSnapshotFile snapshotFile)
    {
        var adapter = FindAdapter(snapshotFile.AdapterId) ?? FindAdapter(snapshotFile.Snapshot.Id);
        if (adapter is not null && _helper.IsHelperPresent)
        {
            var response = Send(HelperCommands.Restore, adapter.Id, snapshotFile.Snapshot);
            if (response.IsSuccess)
            {
                MarkSnapshot(snapshotFile, rollbackArmed: false, kept: true);
                Log("Information", "已回滚到修改前的配置");
                return true;
            }

            Log("Error", $"回滚失败：{response.ToUserText()}");
            return false;
        }

        // 网卡已消失（例如 USB 网卡被拔出）时无法回滚，但快照仍然保留供人工恢复。
        Log("Warning", "网卡不可读或助手不可用，未能自动回滚；未决快照已保留，可在稍后恢复");
        return false;
    }

    private AdapterSnapshot? Readback(string adapterId) => FindAdapter(adapterId);

    // -----------------------------------------------------------------------------------------
    // 内部：错误构造
    // -----------------------------------------------------------------------------------------

    private NetConfigResult AdapterNotFound(string adapterId) => NetConfigResult.Fail(
        ErrorCodes.PlatformAdapterNotFound, AdapterNotFoundError(adapterId).Message);

    private static ErrorInfo AdapterNotFoundError(string adapterId) => new(
        ErrorCodes.PlatformAdapterNotFound,
        ErrorCategory.Platform,
        Severity.Warning,
        $"找不到网卡 {adapterId}：可能已被拔出、禁用或重命名",
        Detail: null,
        Retriable: true,
        Hint: "请刷新网卡列表后重试");

    private static NetConfigResult ValidationFailed(ErrorInfo? error)
    {
        var info = error ?? new ErrorInfo(ErrorCodes.ValidationNetworkConfig, ErrorCategory.Validation,
            Severity.Warning, "配置校验未通过", Hint: "请检查 IP、掩码、网关与 DNS 的填写");
        return NetConfigResult.Fail(info.Code,
            info.Hint is null ? info.Message : $"{info.Message}（{info.Hint}）");
    }

    private NetConfigResult HelperUnavailable() => NetConfigResult.Fail(
        ErrorCodes.PlatformHelperUnavailable,
        $"提权助手不可用（预期路径 {_helper.ExpectedPath}），未修改网卡。{_helper.StatusText}");

    private NetConfigResult SnapshotFailed(string adapterId) => NetConfigResult.Fail(
        ErrorCodes.StorageWriteFailed,
        $"无法写入配置快照（目录 {_snapshotDirectory}），为避免无法恢复，本次未修改网卡",
        Readback(adapterId));

    private void Log(string level, string message) => _log?.Invoke(level, "NetConfig", message);

    /// <summary>快照文件的内容（与 <see cref="AdapterSnapshotFile"/> 一一对应；Path 由文件名决定，不落盘）。</summary>
    private sealed record SnapshotFileDto(
        string AdapterId,
        AdapterSnapshot Snapshot,
        DateTimeOffset CreatedAt,
        string Operation,
        string RequestSummary,
        bool Kept,
        bool RollbackArmed,
        DateTimeOffset? RollbackDeadline);
}
