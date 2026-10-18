using Comm.Core.Errors;
using Comm.Platform.Windows;

namespace Comm.NetConfigHelper;

/// <summary>
/// 可注入的时间源（规划 13.7：时间来源集中管理；测试用固定/手动时钟验证倒计时，不依赖真实等待）。
/// </summary>
public interface ISystemClock
{
    /// <summary>当前 UTC 时间（倒计时只做差值比较，不使用本地时间，避免时区/夏令时干扰）。</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>生产用时间源。</summary>
public sealed class SystemClock : ISystemClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// 安全恢复倒计时（规划 4.8.3 "支持设置安全恢复倒计时…计时由独立的提权助手持有"、12.5、15.8 第 5 步、D-6）。
/// <para>
/// 语义：
/// * <see cref="Arm"/> 武装后，到期（<see cref="Tick"/> 检测到时间已过截止点）<b>只执行一次</b>回滚；
/// * <see cref="Confirm"/>（用户点"保留"）后不再回滚；
/// * 重复 <see cref="Arm"/> 覆盖旧的（新的秒数/动作/描述生效，并重置上一轮的结果状态）；
/// * 确认后可以再次 <see cref="Arm"/>（下一轮修改重新武装）；
/// * 回滚失败/抛异常时结果记入 <see cref="LastError"/>，错误码为 <see cref="ErrorCodes.PlatformNetConfigRollbackFailed"/>
///   （PLT-5006：必须显式提示用户手工恢复），同时 <see cref="RollbackPerformed"/> 仍为 true——表示"回滚动作已经执行过"，
///   保证不会因为反复 Tick 而重复回滚。
/// </para>
/// <para>
/// 线程模型：所有状态由内部锁保护；<see cref="Tick"/> 在锁外执行回滚动作，避免动作自身再进入倒计时时死锁。
/// 计时推进由宿主（<see cref="HelperServer"/>，1 秒定时器）调用 <see cref="Tick"/>，本类不持有定时器，便于测试。
/// </para>
/// </summary>
public sealed class RollbackCountdown
{
    private readonly ISystemClock _clock;
    private readonly object _sync = new();

    private bool _armed;
    private DateTimeOffset _deadline;
    private int _armedSeconds;
    private Func<NetConfigResult>? _rollbackAction;
    private string? _description;
    private bool _rollbackPerformed;
    private string? _lastError;
    private DateTimeOffset? _lastRollbackAtUtc;

    public RollbackCountdown(ISystemClock? clock = null) => _clock = clock ?? SystemClock.Instance;

    /// <summary>当前是否持有未确认的回滚倒计时。</summary>
    public bool IsArmed
    {
        get { lock (_sync) return _armed; }
    }

    /// <summary>剩余秒数（向上取整；未武装时为 0）。</summary>
    public int RemainingSeconds
    {
        get
        {
            lock (_sync)
            {
                if (!_armed) return 0;
                var remaining = (_deadline - _clock.UtcNow).TotalMilliseconds;
                if (remaining <= 0) return 0;
                return (int)Math.Ceiling(remaining / 1000.0);
            }
        }
    }

    /// <summary>本轮武装的秒数（未武装时为 0）。</summary>
    public int ArmedSeconds
    {
        get { lock (_sync) return _armed ? _armedSeconds : 0; }
    }

    /// <summary>本轮描述（例如"回滚到修改前配置（已连接 · 静态 · 192.168.1.10/24 …）"）。</summary>
    public string? Description
    {
        get { lock (_sync) return _description; }
    }

    /// <summary>是否已经执行过回滚（无论成功失败；用于"只回滚一次"的判定依据）。</summary>
    public bool RollbackPerformed
    {
        get { lock (_sync) return _rollbackPerformed; }
    }

    /// <summary>最近一次回滚的错误说明（含 PLT-5006 错误码）；成功或未回滚时为 null。</summary>
    public string? LastError
    {
        get { lock (_sync) return _lastError; }
    }

    /// <summary>最近一次执行回滚的时间（UTC）。</summary>
    public DateTimeOffset? LastRollbackAtUtc
    {
        get { lock (_sync) return _lastRollbackAtUtc; }
    }

    /// <summary>
    /// 武装倒计时。<paramref name="seconds"/> 必须大于 0；不需要回滚时（AutoRollbackSeconds = 0）不要调用本方法。
    /// </summary>
    public void Arm(int seconds, Func<NetConfigResult> rollbackAction, string description)
    {
        ArgumentNullException.ThrowIfNull(rollbackAction);
        if (seconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds,
                "安全恢复倒计时的秒数必须大于 0；不需要回滚时不要武装倒计时（AutoRollbackSeconds = 0）");
        }

        lock (_sync)
        {
            _armed = true;
            _armedSeconds = seconds;
            _deadline = _clock.UtcNow.AddSeconds(seconds);
            _rollbackAction = rollbackAction;
            _description = description ?? string.Empty;

            // 新一轮：清掉上一轮的结果，避免把旧失败误报成本次结果。
            _rollbackPerformed = false;
            _lastError = null;
            _lastRollbackAtUtc = null;
        }
    }

    /// <summary>确认保留当前配置（用户点了"保留"）：解除武装，不再回滚。</summary>
    public void Confirm()
    {
        lock (_sync)
        {
            _armed = false;
            _rollbackAction = null;
            _armedSeconds = 0;
            _deadline = default;
        }
    }

    /// <summary>
    /// 推进计时：到期则执行一次回滚。返回 true 表示本次调用触发并执行了回滚。
    /// 未武装 / 未到期时为无害的空操作。
    /// </summary>
    public bool Tick()
    {
        Func<NetConfigResult>? action;
        lock (_sync)
        {
            if (!_armed) return false;
            if (_clock.UtcNow < _deadline) return false;

            // 关键：先把"_armed"清掉再执行动作，保证同一轮绝不会回滚两次（重入/多线程都安全）。
            action = _rollbackAction;
            _armed = false;
            _rollbackAction = null;
            _armedSeconds = 0;
            _deadline = default;
            _rollbackPerformed = true;
            _lastRollbackAtUtc = _clock.UtcNow;
        }

        if (action is null) return true;

        NetConfigResult? result = null;
        string? error = null;
        try
        {
            result = action();
        }
        catch (Exception ex)
        {
            // 堆栈只留在日志里（调用方写），这里只保留可展示的摘要。
            error = $"{ErrorCodes.PlatformNetConfigRollbackFailed} 自动回滚时发生异常：{ex.GetType().Name}: {ex.Message}";
        }

        if (error is null && result is not null && !result.IsSuccess)
        {
            error = $"{ErrorCodes.PlatformNetConfigRollbackFailed} 自动回滚失败：{result.ToUserText()}";
        }

        lock (_sync)
        {
            _lastError = error;
        }

        return true;
    }
}
