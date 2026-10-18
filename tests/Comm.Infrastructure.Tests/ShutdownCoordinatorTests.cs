using Comm.Core.Tasks;
using Xunit;

namespace Comm.Infrastructure.Tests;

/// <summary>
/// 关闭协调器（M1 反馈问题 5/10）：必须最多执行一次、按顺序执行、失败不阻断后续步骤。
/// </summary>
public class ShutdownCoordinatorTests
{
    [Fact]
    public async Task Steps_run_in_order_and_only_once()
    {
        var order = new List<string>();
        var coordinator = new ShutdownCoordinator(new[]
        {
            new ShutdownStep("一", () => { order.Add("一"); return ValueTask.CompletedTask; }),
            new ShutdownStep("二", () => { order.Add("二"); return ValueTask.CompletedTask; }),
            new ShutdownStep("三", () => { order.Add("三"); return ValueTask.CompletedTask; })
        });

        Assert.False(coordinator.HasRun);
        await coordinator.ShutdownAsync();
        await coordinator.ShutdownAsync(); // 第二次调用必须什么都不做
        await coordinator.ShutdownAsync();
        var failures = await coordinator.ShutdownAsync();

        Assert.Equal(new[] { "一", "二", "三" }, order);
        Assert.Empty(failures);
        Assert.True(coordinator.HasRun);
    }

    [Fact]
    public async Task A_failing_step_does_not_stop_later_steps()
    {
        var ran = new List<string>();
        var coordinator = new ShutdownCoordinator(new[]
        {
            new ShutdownStep("断开会话", () => throw new InvalidOperationException("模拟释放失败")),
            new ShutdownStep("刷新落盘", () => { ran.Add("刷新落盘"); return ValueTask.CompletedTask; }),
            new ShutdownStep("清理运行标记", () => { ran.Add("清理运行标记"); return ValueTask.CompletedTask; })
        });

        var failures = await coordinator.ShutdownAsync();

        Assert.Equal(new[] { "刷新落盘", "清理运行标记" }, ran);
        var failure = Assert.Single(failures);
        Assert.Contains("断开会话", failure);
        Assert.Contains("模拟释放失败", failure);
    }

    [Fact]
    public async Task Concurrent_shutdown_calls_share_a_single_run()
    {
        int runs = 0;
        var coordinator = new ShutdownCoordinator(new[]
        {
            new ShutdownStep("只实现 IAsyncDisposable 的服务", async () =>
            {
                Interlocked.Increment(ref runs);
                await Task.Delay(20);
            })
        });

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => coordinator.ShutdownAsync().AsTask()));

        Assert.Equal(1, runs);
    }

    [Fact]
    public void Synchronous_shutdown_works_for_the_wpf_exit_path()
    {
        var ran = false;
        var coordinator = new ShutdownCoordinator(new[]
        {
            new ShutdownStep("同步执行", () => { ran = true; return ValueTask.CompletedTask; })
        });

        var failures = coordinator.Shutdown();

        Assert.Empty(failures);
        Assert.True(ran);
    }
}
