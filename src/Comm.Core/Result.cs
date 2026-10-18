using Comm.Core.Errors;

namespace Comm.Core;

/// <summary>业务结果包装：预期内的失败用值表达，不抛异常（见规划 15.1）。</summary>
public readonly struct Result<T>
{
    private Result(bool ok, T? value, ErrorInfo? error)
    {
        IsSuccess = ok;
        Value = value;
        Error = error;
    }

    public bool IsSuccess { get; }
    public T? Value { get; }
    public ErrorInfo? Error { get; }

    public static Result<T> Ok(T value) => new(true, value, null);
    public static Result<T> Fail(ErrorInfo error) => new(false, default, error);

    public bool TryGet(out T value, out ErrorInfo? error)
    {
        value = Value!;
        error = Error;
        return IsSuccess;
    }
}
