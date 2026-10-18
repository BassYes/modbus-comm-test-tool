namespace Comm.NetConfigHelper.Tests.Fakes;

/// <summary>手动时钟（注入 <see cref="ISystemClock"/>，避免测试真的等待 60 秒）。</summary>
internal sealed class ManualClock : ISystemClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan delta) => UtcNow += delta;

    public void AdvanceSeconds(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
}
