namespace Comm.Core.Tasks;

/// <summary>
/// 可等待的暂停门：任务在当前请求结束后的下一个边界停下（见规划 13.4.1）。
/// </summary>
public sealed class PauseGate
{
    private readonly object _sync = new();
    private TaskCompletionSource<bool>? _resume;

    public bool IsPaused
    {
        get { lock (_sync) return _resume is not null; }
    }

    public void Pause()
    {
        lock (_sync)
        {
            _resume ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource<bool>? tcs;
        lock (_sync)
        {
            tcs = _resume;
            _resume = null;
        }
        tcs?.TrySetResult(true);
    }

    /// <summary>若处于暂停状态则等待恢复；取消时抛出。</summary>
    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        Task? pending;
        lock (_sync) pending = _resume?.Task;
        if (pending is null) return;
        await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
