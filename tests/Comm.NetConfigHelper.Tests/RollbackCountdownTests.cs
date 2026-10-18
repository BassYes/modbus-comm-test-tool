using Comm.Core.Errors;
using Comm.NetConfigHelper.Tests.Fakes;
using Comm.Platform.Windows;
using Xunit;

namespace Comm.NetConfigHelper.Tests;

/// <summary>
/// 安全恢复倒计时（规划 4.8.3、12.5、15.8、D-6）。
/// 全部用注入的手动时钟推进时间，不依赖真实等待。
/// </summary>
public sealed class RollbackCountdownTests
{
    [Fact]
    public void Tick_WhenExpired_RollsBackExactlyOnce()
    {
        var clock = new ManualClock();
        var countdown = new RollbackCountdown(clock);
        int calls = 0;

        countdown.Arm(3, () => { calls++; return NetConfigResult.Ok(); }, "回滚到修改前配置");

        Assert.True(countdown.IsArmed);
        Assert.False(countdown.Tick());  // 未到期

        clock.AdvanceSeconds(3);

        Assert.True(countdown.Tick());
        Assert.Equal(1, calls);
        Assert.True(countdown.RollbackPerformed);
        Assert.Null(countdown.LastError);
        Assert.False(countdown.IsArmed);

        // 再推进再 Tick：绝不能再回滚第二次
        clock.AdvanceSeconds(60);
        Assert.False(countdown.Tick());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Tick_BeforeDeadline_DoesNotRollBack()
    {
        var clock = new ManualClock();
        var countdown = new RollbackCountdown(clock);
        int calls = 0;

        countdown.Arm(60, () => { calls++; return NetConfigResult.Ok(); }, "回滚");

        clock.AdvanceSeconds(59);
        Assert.False(countdown.Tick());
        Assert.Equal(0, calls);
        Assert.True(countdown.IsArmed);
        Assert.Equal(1, countdown.RemainingSeconds);
    }

    [Fact]
    public void Confirm_BeforeDeadline_NeverRollsBack()
    {
        var clock = new ManualClock();
        var countdown = new RollbackCountdown(clock);
        int calls = 0;

        countdown.Arm(5, () => { calls++; return NetConfigResult.Ok(); }, "回滚");
        countdown.Confirm();

        Assert.False(countdown.IsArmed);
        Assert.Equal(0, countdown.RemainingSeconds);

        clock.AdvanceSeconds(600);
        Assert.False(countdown.Tick());
        Assert.Equal(0, calls);
        Assert.False(countdown.RollbackPerformed);
    }

    [Fact]
    public void Arm_Twice_ReplacesPreviousTimer()
    {
        var clock = new ManualClock();
        var countdown = new RollbackCountdown(clock);
        int first = 0, second = 0;

        countdown.Arm(300, () => { first++; return NetConfigResult.Ok(); }, "第一次");
        clock.AdvanceSeconds(10);
        countdown.Arm(2, () => { second++; return NetConfigResult.Ok(); }, "第二次");

        Assert.Equal(2, countdown.RemainingSeconds);

        clock.AdvanceSeconds(2);
        Assert.True(countdown.Tick());
        Assert.Equal(0, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public void Confirm_ThenArmAgain_CanRearm()
    {
        var clock = new ManualClock();
        var countdown = new RollbackCountdown(clock);
        int calls = 0;

        countdown.Arm(5, () => { calls++; return NetConfigResult.Ok(); }, "第一轮");
        countdown.Confirm();
        Assert.False(countdown.IsArmed);

        // 用户确认后再次修改：必须可以重新武装（R-07 反复改 IP 的常规流程）
        countdown.Arm(2, () => { calls++; return NetConfigResult.Ok(); }, "第二轮");
        Assert.True(countdown.IsArmed);
        Assert.False(countdown.RollbackPerformed);
        Assert.Null(countdown.LastError);

        clock.AdvanceSeconds(2);
        Assert.True(countdown.Tick());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Tick_WhenRollbackFails_RecordsPlt5006()
    {
        var clock = new ManualClock();
        var countdown = new RollbackCountdown(clock);

        countdown.Arm(1, () => NetConfigResult.Fail(ErrorCodes.PlatformNetConfigApplyFailed, "后端拒绝写入"), "回滚");
        clock.AdvanceSeconds(1);

        Assert.True(countdown.Tick());
        Assert.True(countdown.RollbackPerformed);
        Assert.NotNull(countdown.LastError);
        Assert.Contains(ErrorCodes.PlatformNetConfigRollbackFailed, countdown.LastError!);
        Assert.Contains("后端拒绝写入", countdown.LastError!);
    }

    [Fact]
    public void Tick_WhenRollbackThrows_RecordsPlt5006()
    {
        var clock = new ManualClock();
        var countdown = new RollbackCountdown(clock);

        countdown.Arm(1, () => throw new InvalidOperationException("pipe broken"), "回滚");
        clock.AdvanceSeconds(1);

        Assert.True(countdown.Tick());
        Assert.NotNull(countdown.LastError);
        Assert.Contains(ErrorCodes.PlatformNetConfigRollbackFailed, countdown.LastError!);
        Assert.Contains("pipe broken", countdown.LastError!);
    }

    [Fact]
    public void RemainingSeconds_CountsDown()
    {
        var clock = new ManualClock();
        var countdown = new RollbackCountdown(clock);

        countdown.Arm(10, () => NetConfigResult.Ok(), "回滚");
        Assert.Equal(10, countdown.RemainingSeconds);

        clock.Advance(TimeSpan.FromSeconds(2.5));
        Assert.Equal(8, countdown.RemainingSeconds);  // 向上取整，剩余 7.5 s → 8

        clock.AdvanceSeconds(7.5);
        Assert.Equal(0, countdown.RemainingSeconds);

        clock.AdvanceSeconds(100);
        Assert.Equal(0, countdown.RemainingSeconds);
    }

    [Fact]
    public void Arm_WithNonPositiveSeconds_Throws()
    {
        var countdown = new RollbackCountdown(new ManualClock());

        Assert.Throws<ArgumentOutOfRangeException>(() => countdown.Arm(0, () => NetConfigResult.Ok(), "回滚"));
        Assert.Throws<ArgumentOutOfRangeException>(() => countdown.Arm(-5, () => NetConfigResult.Ok(), "回滚"));
        Assert.False(countdown.IsArmed);
    }
}
