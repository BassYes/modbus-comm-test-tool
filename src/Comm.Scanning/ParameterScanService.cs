using Comm.Core.Errors;
using Comm.Core.Tasks;
using Comm.Core.Transport;
using Comm.Session;

namespace Comm.Scanning;

/// <summary>
/// 串口重配接口（规划 22.3.4 第 2 点）。
/// <para>
/// 参数扫描必须在运行中更换波特率/校验位，而更换参数 = 关串口 + 重开 + 清缓冲 + 稳定等待。
/// 该能力通过接口注入，由界面层（<c>SessionManager</c>）实现，<c>Comm.Scanning</c> 不引用 WPF 或具体传输，保持分层。
/// </para>
/// </summary>
public interface ISerialReconfigurator
{
    /// <summary>当前是否是可重配的串口会话（TCP 会话返回 false，参数扫描不适用）。</summary>
    bool CanReconfigure { get; }

    /// <summary>扫描前的原始串口参数（用于界面显示“原 4800-8-N-1”与恢复）。</summary>
    SerialSettings? OriginalSettings { get; }

    /// <summary>当前实际打开的串口参数。</summary>
    SerialSettings? ActiveSettings { get; }

    /// <summary>
    /// 切换到指定串口参数并返回可用的会话；失败返回 null（已记录原因）。
    /// <paramref name="settleMs"/> 为重开端口后的稳定等待（USB 转串口通常 100～500 ms）。
    /// </summary>
    Task<ModbusSession?> ApplyAsync(SerialSettings settings, int settleMs, CancellationToken cancellationToken);

    /// <summary>恢复到扫描前的原始参数。**四种终态都必须调用**（规划 22.3.4 第 3 点）。</summary>
    Task<SerialRestoreResult> RestoreAsync(CancellationToken cancellationToken);
}

/// <summary>恢复原参数的结果。</summary>
public sealed record SerialRestoreResult(bool Success, string? Error)
{
    public static readonly SerialRestoreResult Ok = new(true, null);
}

/// <summary>单个参数组合的分组结果（多组合同时命中时按组合分组展示，规划 22.3.3）。</summary>
public sealed record ParameterScanGroup(
    SerialCombination Combination,
    int Online,
    int Suspected,
    int Offline,
    int NoisyAddresses,
    bool Recommended,
    string Note);

/// <summary>参数扫描的完整结果。</summary>
public sealed record ParameterScanResult(
    IReadOnlyList<DeviceScanItem> Items,
    IReadOnlyList<ParameterScanGroup> Groups,
    IReadOnlyList<SerialCombination> TestedCombinations,
    IReadOnlyList<SerialCombination> HitCombinations,
    bool Restored,
    string? RestoreError,
    IReadOnlyList<string> Failures)
{
    public IReadOnlyList<DeviceScanItem> OnlineItems =>
        Items.Where(i => i.Status == DeviceOnlineStatus.Online).ToArray();

    /// <summary>推荐结果（供“应用到连接中心”默认选中）。</summary>
    public DeviceScanItem? BestRecommendation => Items.FirstOrDefault(i => i.Recommended);

    public string Summary
    {
        get
        {
            if (HitCombinations.Count == 0)
            {
                return $"未在任何参数组合上发现设备（测试 {TestedCombinations.Count} 组）；" +
                       "请确认设备已上电、接线正确，并检查地址范围。";
            }

            var best = BestRecommendation;
            var text = $"在 {HitCombinations.Count} 个参数组合上发现响应（共测试 {TestedCombinations.Count} 组）";
            if (best is not null)
                text += $"；推荐 {best.CombinationText}（地址 {best.Address}）";
            if (HitCombinations.Count > 1)
                text += "。多个组合同时命中时请以“推荐”为参考并用设备手册确认，程序不做唯一断言。";
            if (!Restored) text += "。⚠ 原串口参数恢复失败，已在连接中心置为未连接，请手工重新连接。";
            return text;
        }
    }
}

/// <summary>
/// 设备扫描页的“未知波特率 / 未知校验位”两阶段扫描（R-11，规划 4.2.6 / 22.3.3）。
/// <para>
/// 阶段 A 定位参数：每个组合只在少量候选地址上发**一次只读探测**（默认 03 读 1 个寄存器、起始地址 0），
/// 合法正常响应或合法异常响应都算命中。
/// 阶段 B 扫描地址：在命中的组合上按 4.2.2 规则扫描完整地址范围，并用 3/3 确认区分“在线/疑似”。
/// </para>
/// <para>避免“组合数 × 247 地址 × 4 模板”的小时级耗时；且始终受 CC-1/CC-6 约束（只读、单事务、无广播）。</para>
/// </summary>
public sealed class ParameterScanService
{
    private readonly Func<ModbusSession?> _session;
    private readonly ISerialReconfigurator _reconfigurator;
    private readonly Action<string>? _log;

    public ParameterScanService(Func<ModbusSession?> session, ISerialReconfigurator reconfigurator, Action<string>? log = null)
    {
        _session = session;
        _reconfigurator = reconfigurator;
        _log = log;
    }

    /// <summary>
    /// 最近一次扫描结束时的"原串口参数恢复结果"。审计 F-06：
    /// 正常结束时它同时体现在 <see cref="ParameterScanResult.Restored"/> 里；
    /// 取消/异常终态不会返回结果对象，界面必须靠本属性判断，不能无条件宣称"已恢复"。
    /// 尚未执行过扫描时为 null。
    /// </summary>
    public SerialRestoreResult? LastRestore { get; private set; }

    public async Task<ParameterScanResult> ScanAsync(ParameterScanOptions options, PauseGate? gate,
        IProgress<ScanProgress>? progress, IProgress<DeviceScanItem>? itemProgress, CancellationToken cancellationToken)
    {
        // 新一轮扫描：先清掉上一轮的恢复结果，避免任何终态误报（审计 F-06）。放在校验之前，
        // 保证"调用方再读到的 LastRestore 一定是本次扫描的结果，或者是 null（本次没走到恢复）"。
        LastRestore = null;

        if (!ParameterScanOptions.Validate(options, out var error)) throw new CommException(error!);
        if (!_reconfigurator.CanReconfigure)
        {
            throw new CommException(new ErrorInfo(ErrorCodes.ValidationParameterScanNotSerial, ErrorCategory.Validation,
                Severity.Warning,
                "未知波特率/校验位扫描需要串口会话；当前是 TCP 会话或不支持重配的会话",
                Hint: "请在连接中心选择串口并连接后再使用该功能"));
        }

        var combinations = options.BuildCombinations();
        var phaseATargets = options.PhaseATargets();
        var portName = _reconfigurator.OriginalSettings?.PortName;

        var items = new List<DeviceScanItem>();
        var groups = new List<ParameterScanGroup>();
        var tested = new List<SerialCombination>();
        var hits = new List<SerialCombination>();
        var failures = new List<string>();
        bool restored = false;
        string? restoreError = null;

        _log?.Invoke($"开始参数扫描：{combinations.Count} 个组合，阶段 A 地址 {string.Join(",", phaseATargets)}，" +
                     $"地址范围 {options.StartAddress}~{options.EndAddress}");

        try
        {
            for (int index = 0; index < combinations.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var combination = combinations[index];
                if (gate is not null)
                {
                    if (gate.IsPaused)
                        progress?.Report(new ScanProgress("参数扫描", index, combinations.Count,
                            $"{combination.Text} 已暂停", ScanStatus.Paused));
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                progress?.Report(new ScanProgress("参数扫描", index, combinations.Count,
                    $"切换串口参数到 {combination.Text}", ScanStatus.Running));

                if (string.IsNullOrEmpty(portName))
                {
                    failures.Add($"{combination.Text}：无法确定串口名，已跳过");
                    continue;
                }

                // 每个组合切换都写一条说明记录（规划 22.3.4 第 7 点；不产生 TX/RX 假帧）。
                _log?.Invoke($"参数扫描：切换到 {portName} {combination.Text}（第 {index + 1}/{combinations.Count} 组）");

                var session = await _reconfigurator
                    .ApplyAsync(combination.ToSettings(portName!), options.ReconfigureSettleMs, cancellationToken)
                    .ConfigureAwait(false);

                if (session is null)
                {
                    failures.Add($"{combination.Text}：串口重配失败，已跳过该组合");
                    continue;
                }

                tested.Add(combination);

                // ---------- 阶段 A：定位参数（每地址只发一次只读探测）
                int noisy = 0;
                bool hit = false;
                var phaseAOptions = options.ToDeviceScanOptions(combination, forPhaseA: true);
                var service = new DeviceScanService(session);

                foreach (var address in phaseATargets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (gate is not null) await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

                    var probe = await service.ProbeAddressAsync(address, phaseAOptions, cancellationToken, combination)
                        .ConfigureAwait(false);
                    noisy += probe.NoiseFrames;

                    if (probe.Status == DeviceOnlineStatus.Online)
                    {
                        hit = true;
                        _log?.Invoke($"参数扫描：{combination.Text} 阶段 A 命中（地址 {address}，{probe.Note}）");
                        break;
                    }

                    if (options.IntervalMs > 0)
                        await Task.Delay(options.IntervalMs, cancellationToken).ConfigureAwait(false);
                }

                if (!hit)
                {
                    groups.Add(new ParameterScanGroup(combination, 0, 0, phaseATargets.Count, noisy, false,
                        noisy > 0
                            ? $"该组合收到杂乱数据（{noisy} 个校验错误帧），设备可能存在但参数不完全匹配"
                            : "阶段 A 无响应"));

                    progress?.Report(new ScanProgress("参数扫描", index + 1, combinations.Count,
                        $"{combination.Text} 未命中", ScanStatus.Running));
                    continue;
                }

                hits.Add(combination);

                // ---------- 阶段 B：在该组合上扫描完整地址范围（3/3 确认由 DeviceScanService 完成）
                progress?.Report(new ScanProgress("参数扫描", index, combinations.Count,
                    $"{combination.Text} 命中，开始扫描地址 {options.StartAddress}~{options.EndAddress}", ScanStatus.Running));

                var scanOptions = options.ToDeviceScanOptions(combination, forPhaseA: false);
                var scanned = await service.ScanAsync(scanOptions, gate, null, itemProgress, cancellationToken, combination)
                    .ConfigureAwait(false);

                items.AddRange(scanned);

                int online = scanned.Count(i => i.Status == DeviceOnlineStatus.Online);
                int suspected = scanned.Count(i => i.Status == DeviceOnlineStatus.Suspected);
                int offline = scanned.Count(i => i.Status == DeviceOnlineStatus.Offline);
                int noisyAddresses = scanned.Count(i => i.NoiseFrames > 0);

                groups.Add(new ParameterScanGroup(combination, online, suspected, offline, noisyAddresses, false,
                    online > 0
                        ? $"找到 {online} 个在线地址{(suspected > 0 ? $"，{suspected} 个疑似（未通过 3/3 确认）" : string.Empty)}"
                        : noisyAddresses > 0
                            ? $"阶段 A 命中但地址扫描未确认在线；{noisyAddresses} 个地址收到杂乱数据"
                            : "阶段 A 命中但阶段 B 未确认在线，建议用已知地址复核"));

                if (!options.ContinueAfterHit) break;
            }
        }
        finally
        {
            // 停止、完成、异常、窗口关闭四种终态都必须恢复原参数（规划 22.3.4 第 3 点）。
            // 恢复用 None 令牌，保证取消路径也会执行；由实现方自行设置超时。
            var restore = await _reconfigurator.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
            restored = restore.Success;
            restoreError = restore.Error;
            LastRestore = restore;   // 审计 F-06：取消/异常路径也要能读到恢复结果
            if (!restored)
            {
                _log?.Invoke($"参数扫描：恢复原串口参数失败 —— {restoreError}");
            }
            else
            {
                _log?.Invoke("参数扫描：已恢复原串口参数");
            }
        }

        var finalGroups = ApplyRecommendation(items, groups);
        var orderedItems = items
            .OrderBy(i => i.Combination is null ? 0 : groups.FindIndex(g => g.Combination == i.Combination))
            .ThenBy(i => i.Address)
            .ToArray();

        progress?.Report(new ScanProgress("参数扫描", combinations.Count, combinations.Count,
            hits.Count == 0 ? "完成：未发现设备" : $"完成：命中 {hits.Count} 个组合", ScanStatus.Completed));

        return new ParameterScanResult(orderedItems, finalGroups, tested, hits, restored, restoreError, failures);
    }

    /// <summary>
    /// 推荐规则（规划 22.3.3）：响应无 CRC 错误、3/3 确认通过、停止位较少者优先；
    /// **不自行断言唯一正确**，只标一个“推荐”供用户确认。
    /// </summary>
    private static IReadOnlyList<ParameterScanGroup> ApplyRecommendation(List<DeviceScanItem> items,
        List<ParameterScanGroup> groups)
    {
        var candidates = groups.Where(g => g.Online > 0).ToArray();
        if (candidates.Length == 0) return groups;

        var best = candidates
            .OrderBy(g => g.NoisyAddresses)                 // 无杂乱数据优先
            .ThenByDescending(g => g.Online)                // 在线地址多优先
            .ThenBy(g => g.Combination.StopBitCount)        // 停止位少优先
            .ThenBy(g => g.Combination.BaudRate)            // 稳定排序
            .First();

        var result = new List<ParameterScanGroup>(groups.Count);
        foreach (var group in groups)
        {
            if (group.Combination != best.Combination)
            {
                result.Add(group);
                continue;
            }

            result.Add(group with
            {
                Recommended = true,
                Note = group.Note + "；推荐（响应无 CRC 错误、3/3 确认通过、停止位较少）"
            });
        }

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Combination == best.Combination) items[i] = items[i] with { Recommended = true };
        }

        return result;
    }
}
