namespace Comm.Core.Tasks;

/// <summary>一个可命名的异步关闭步骤。</summary>
public sealed record ShutdownStep(string Name, Func<ValueTask> Action);

/// <summary>
/// 关闭协调器（见规划 13.6、M1 反馈问题 5/10）。
/// 职责：
/// 1. **最多执行一次**——退出路径可能被多个入口触发（关闭按钮、Alt+F4、双击标题栏图标、系统关机），重复释放会抛异常；
/// 2. **按顺序执行**并逐个隔离异常，任何一步失败都不阻断后续步骤（否则会漏掉落盘刷新与 .running 清理）；
/// 3. 记录失败项供诊断，**不弹窗**（退出阶段再弹“内部错误”会形成递归提示）。
/// </summary>
public sealed class ShutdownCoordinator
{
    private readonly IReadOnlyList<ShutdownStep> _steps;
    private readonly List<string> _failures = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _hasRun;

    public ShutdownCoordinator(IEnumerable<ShutdownStep> steps)
    {
        _steps = steps?.ToArray() ?? throw new ArgumentNullException(nameof(steps));
    }

    /// <summary>是否已经执行过（幂等保证）。</summary>
    public bool HasRun => _hasRun;

    /// <summary>按顺序执行所有步骤；返回失败步骤的描述（空表示全部成功）。</summary>
    public async ValueTask<IReadOnlyList<string>> ShutdownAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_hasRun) return _failures.ToArray();
            _hasRun = true;

            foreach (var step in _steps)
            {
                try
                {
                    await step.Action().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 退出阶段的异常只记录，不再上抛/弹窗（见 M1 反馈问题 5）。
                    _failures.Add($"{step.Name}：{ex.GetType().Name} {ex.Message}");
                }
            }

            return _failures.ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>同步执行（WPF 的 OnExit 是同步入口）。</summary>
    public IReadOnlyList<string> Shutdown() => ShutdownAsync().AsTask().GetAwaiter().GetResult();
}
