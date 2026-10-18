namespace Comm.Scanning;

/// <summary>任务状态（见规划 13.4.1）。</summary>
public enum ScanStatus
{
    Pending,
    Running,
    Paused,
    Completed,
    Cancelled,
    Faulted
}

public sealed record ScanProgress(string TaskName, int Completed, int Total, string CurrentStep, ScanStatus Status)
{
    public double Percent => Total <= 0 ? 0 : Math.Min(100.0, Completed * 100.0 / Total);

    public string ToText() => $"{TaskName}：{Completed}/{Total}（{Percent:F1}%）{CurrentStep}";
}

/// <summary>数据类型（见规划 4.3.3）。</summary>
public enum DataType
{
    Bit,
    UInt16,
    Int16,
    UInt32,
    Int32,
    Float32,
    UInt64,
    Int64,
    Float64,
    Hex,
    Binary,
    Bcd,
    Ascii
}

/// <summary>多寄存器数值的字节序 / 字序组合（见规划 14.6.2）。</summary>
public enum ByteOrder
{
    /// <summary>A B C D：大端，与报文顺序一致。</summary>
    Abcd,
    /// <summary>B A D C：字节交换（每个寄存器内字节交换）。</summary>
    Badc,
    /// <summary>C D A B：字交换。</summary>
    Cdab,
    /// <summary>D C B A：全反转。</summary>
    Dcba
}

public static class DataTypeExtensions
{
    public static int RegisterCount(this DataType type) => type switch
    {
        DataType.UInt32 or DataType.Int32 or DataType.Float32 => 2,
        DataType.UInt64 or DataType.Int64 or DataType.Float64 => 4,
        _ => 1
    };

    public static string ToChinese(this DataType type) => type switch
    {
        DataType.Bit => "位 (Bool)",
        DataType.UInt16 => "无符号 16 位",
        DataType.Int16 => "有符号 16 位",
        DataType.UInt32 => "无符号 32 位",
        DataType.Int32 => "有符号 32 位",
        DataType.Float32 => "浮点 32 位",
        DataType.UInt64 => "无符号 64 位",
        DataType.Int64 => "有符号 64 位",
        DataType.Float64 => "浮点 64 位",
        DataType.Hex => "十六进制",
        DataType.Binary => "二进制",
        DataType.Bcd => "BCD",
        DataType.Ascii => "ASCII 文本",
        _ => type.ToString()
    };

    public static string ToDisplay(this ByteOrder order) => order switch
    {
        ByteOrder.Abcd => "ABCD（大端）",
        ByteOrder.Badc => "BADC（字节交换）",
        ByteOrder.Cdab => "CDAB（字交换）",
        _ => "DCBA（全反转）"
    };
}
