namespace Comm.Protocols.Modbus;

/// <summary>
/// RTU 时序计算（规划 14.3.2、22.3.4 第 1 点）。
/// <para>
/// 静默阈值原先是固定 5 ms（<c>SessionManager.SilenceThresholdMs</c>），在低波特率下会误拆帧：
/// 1200 bps 时 3.5 个字符约 32 ms，仍然按 5 ms 判静默，会把一帧当成两帧。
/// 未知波特率扫描会遍历 1200～115200，因此阈值必须随波特率变化。
/// </para>
/// </summary>
public static class RtuTiming
{
    /// <summary>一个字符的位数：1 起始位 + 8 数据位 + 1 校验位 + 1 停止位。</summary>
    public const int BitsPerCharacter = 11;

    /// <summary>
    /// Windows + USB 转串口下可靠使用的静默下限；高波特率时公式结果更小，取这个值兜底。
    /// </summary>
    public const int MinimumSilenceThresholdMs = 5;

    /// <summary>静默阈值的安全余量（毫秒），吸收计时抖动与 USB 批量传输延迟。</summary>
    public const int SilenceSafetyMarginMs = 1;

    /// <summary>单字符传输时间（毫秒）。</summary>
    public static double CharacterTimeMs(int baudRate) =>
        1000.0 * BitsPerCharacter / Math.Max(1, baudRate);

    /// <summary>指定字节数的帧在同波特率下的传输时间（毫秒）。</summary>
    public static double FrameTimeMs(int baudRate, int byteCount) =>
        CharacterTimeMs(baudRate) * Math.Max(1, byteCount);

    /// <summary>
    /// 静默阈值：<c>max(用户阈值, ⌈3.5 × 11 位 ÷ 波特率⌉ + 余量, 5 ms)</c>。
    /// </summary>
    public static int ComputeSilenceThresholdMs(int baudRate, int userThresholdMs = MinimumSilenceThresholdMs)
    {
        double fromFormula = 3.5 * CharacterTimeMs(baudRate) + SilenceSafetyMarginMs;
        int formula = (int)Math.Ceiling(fromFormula);
        return Math.Max(Math.Max(1, userThresholdMs), Math.Max(MinimumSilenceThresholdMs, formula));
    }

    /// <summary>
    /// 响应超时下限：请求帧 + 响应帧的传输时间之和，再加设备处理余量。
    /// 低波特率下若仍用固定 500 ms，短帧可能还没发完就被判超时。
    /// </summary>
    public static int MinimumResponseTimeoutMs(int baudRate, int frameBytes = 8, int deviceProcessingMs = 100) =>
        (int)Math.Ceiling(FrameTimeMs(baudRate, frameBytes) * 2) + Math.Max(0, deviceProcessingMs);

    /// <summary>把用户填写的响应超时抬到该波特率下的合理下限。</summary>
    public static int EffectiveResponseTimeoutMs(int userTimeoutMs, int baudRate, int frameBytes = 8) =>
        Math.Max(Math.Max(1, userTimeoutMs), MinimumResponseTimeoutMs(baudRate, frameBytes));
}
