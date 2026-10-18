namespace Comm.Core.Codecs;

/// <summary>
/// 主站模式下成帧器的长度预测提示（见规划 14.3.2）：已知当前请求时，
/// 可精确预测响应长度，并在流中跳过不属于本次响应的字节。
/// </summary>
public interface IExpectationAwareFramer
{
    void SetExpectation(byte? unitId, byte? functionCode);
}
