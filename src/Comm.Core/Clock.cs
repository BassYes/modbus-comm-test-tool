using System.Diagnostics;

namespace Comm.Core;

/// <summary>时间来源：显示用本地时间，间隔/耗时用单调时钟（见规划 13.7）。</summary>
public static class Clock
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();

    /// <summary>单调时钟 tick（Stopwatch 频率）。</summary>
    public static long MonotonicTicks => Watch.ElapsedTicks;

    /// <summary>单调时钟频率。</summary>
    public static long Frequency => Stopwatch.Frequency;

    public static DateTimeOffset Now => DateTimeOffset.Now;

    public static double TicksToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public static double MillisecondsBetween(long fromTicks, long toTicks) => TicksToMilliseconds(toTicks - fromTicks);
}
