namespace Comm.Protocols.Modbus;

/// <summary>数据区（四类标准 Modbus 数据区，见规划 4.3.1）。</summary>
public enum DataArea
{
    Coils = 0x01,
    DiscreteInputs = 0x02,
    HoldingRegisters = 0x03,
    InputRegisters = 0x04
}

public static class DataAreaExtensions
{
    public static byte ReadFunctionCode(this DataArea area) => (byte)area;

    public static bool IsBitArea(this DataArea area) => area is DataArea.Coils or DataArea.DiscreteInputs;

    public static bool IsWritable(this DataArea area) => area is DataArea.Coils or DataArea.HoldingRegisters;

    public static string ToChinese(this DataArea area) => area switch
    {
        DataArea.Coils => "线圈 (01)",
        DataArea.DiscreteInputs => "离散输入 (02)",
        DataArea.HoldingRegisters => "保持寄存器 (03)",
        DataArea.InputRegisters => "输入寄存器 (04)",
        _ => area.ToString()
    };

    public static string ToShortChinese(this DataArea area) => area switch
    {
        DataArea.Coils => "线圈",
        DataArea.DiscreteInputs => "离散输入",
        DataArea.HoldingRegisters => "保持寄存器",
        DataArea.InputRegisters => "输入寄存器",
        _ => area.ToString()
    };
}

/// <summary>功能码常量与协议限制（见规划 14.4）。</summary>
public static class Fn
{
    public const byte ReadCoils = 0x01;
    public const byte ReadDiscreteInputs = 0x02;
    public const byte ReadHoldingRegisters = 0x03;
    public const byte ReadInputRegisters = 0x04;
    public const byte WriteSingleCoil = 0x05;
    public const byte WriteSingleRegister = 0x06;
    public const byte ReadExceptionStatus = 0x07;
    public const byte Diagnostics = 0x08;
    public const byte WriteMultipleCoils = 0x0F;
    public const byte WriteMultipleRegisters = 0x10;
    public const byte MaskWriteRegister = 0x16;
    public const byte ReadWriteMultipleRegisters = 0x17;
    public const byte ReadFileRecord = 0x14;
    public const byte ReadDeviceIdentification = 0x2B;

    public static bool IsWrite(byte functionCode) =>
        functionCode is WriteSingleCoil or WriteSingleRegister or WriteMultipleCoils or WriteMultipleRegisters
            or MaskWriteRegister or ReadWriteMultipleRegisters;

    public static bool IsRead(byte functionCode) =>
        functionCode is ReadCoils or ReadDiscreteInputs or ReadHoldingRegisters or ReadInputRegisters
            or ReadExceptionStatus or ReadFileRecord or ReadDeviceIdentification;

    public static bool IsException(byte functionCode) => (functionCode & 0x80) != 0;

    public static string Name(byte functionCode)
    {
        if (IsException(functionCode))
            return $"异常响应 ({Name((byte)(functionCode & 0x7F))})";

        return functionCode switch
        {
            ReadCoils => "读线圈",
            ReadDiscreteInputs => "读离散输入",
            ReadHoldingRegisters => "读保持寄存器",
            ReadInputRegisters => "读输入寄存器",
            WriteSingleCoil => "写单个线圈",
            WriteSingleRegister => "写单个寄存器",
            ReadExceptionStatus => "读异常状态",
            Diagnostics => "诊断",
            WriteMultipleCoils => "写多个线圈",
            WriteMultipleRegisters => "写多个寄存器",
            MaskWriteRegister => "掩码写寄存器",
            ReadWriteMultipleRegisters => "读写多个寄存器",
            _ => $"功能码 0x{functionCode:X2}"
        };
    }

    /// <summary>单次请求的点数上限；未知功能码返回 0 表示不做限制。</summary>
    public static int MaxQuantity(byte functionCode) => functionCode switch
    {
        ReadCoils or ReadDiscreteInputs => 2000,
        ReadHoldingRegisters or ReadInputRegisters => 125,
        WriteMultipleCoils => 1968,
        WriteMultipleRegisters => 123,
        _ => 0
    };
}

/// <summary>Modbus 异常码（见规划 15.4）。</summary>
public static class ModbusExceptions
{
    public static string Describe(byte code) => code switch
    {
        0x01 => "非法功能码：设备不支持该功能",
        0x02 => "非法数据地址：地址不存在或起始地址加数量越界",
        0x03 => "非法数据值：数量或数据字段不合规",
        0x04 => "从站设备故障：设备内部错误",
        0x05 => "确认：设备已接受，需后续查询",
        0x06 => "从站设备忙：设备处理中，可稍后重试",
        0x07 => "否定确认：设备无法执行该功能",
        0x08 => "存储奇偶性差错",
        0x0A => "网关路径不可用",
        0x0B => "网关目标设备无响应",
        _ => $"未知异常码 0x{code:X2}"
    };

    public static string Hint(byte code) => code switch
    {
        0x01 => "换用其他功能码，或核对设备手册",
        0x02 => "缩小地址范围，并检查地址是否为 0 基",
        0x03 => "降低单次请求数量",
        0x04 => "检查设备运行状态",
        0x06 => "稍后重试",
        0x0A or 0x0B => "检查网关后级线路与从站地址",
        _ => "核对设备手册"
    };

    /// <summary>异常响应是否值得重试（06 忙可延时重试，其余重试无意义）。</summary>
    public static bool IsRetriable(byte code) => code == 0x06;
}
