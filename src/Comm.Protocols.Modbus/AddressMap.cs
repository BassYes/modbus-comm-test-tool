namespace Comm.Protocols.Modbus;

/// <summary>地址显示风格（见规划 6.1）。转换集中在本类，界面与导出均通过它。</summary>
public enum AddressStyle
{
    /// <summary>协议地址（0 基），报文中的实际地址。</summary>
    ProtocolZeroBased,

    /// <summary>文档地址（1 基）。</summary>
    DocumentOneBased,

    /// <summary>Modicon 风格：0xxxx / 1xxxx / 3xxxx / 4xxxx。</summary>
    Modicon
}

public static class AddressMap
{
    /// <summary>显示地址 → 协议地址。</summary>
    public static int ToProtocolAddress(int displayAddress, AddressStyle style, DataArea area) => style switch
    {
        AddressStyle.ProtocolZeroBased => displayAddress,
        AddressStyle.DocumentOneBased => displayAddress - 1,
        AddressStyle.Modicon => displayAddress - ModiconBase(area),
        _ => displayAddress
    };

    /// <summary>协议地址 → 显示地址。</summary>
    public static int ToDisplayAddress(int protocolAddress, AddressStyle style, DataArea area) => style switch
    {
        AddressStyle.ProtocolZeroBased => protocolAddress,
        AddressStyle.DocumentOneBased => protocolAddress + 1,
        AddressStyle.Modicon => protocolAddress + ModiconBase(area),
        _ => protocolAddress
    };

    public static string Format(int protocolAddress, AddressStyle style, DataArea area)
    {
        int display = ToDisplayAddress(protocolAddress, style, area);
        return style == AddressStyle.Modicon ? display.ToString("D5") : display.ToString();
    }

    private static int ModiconBase(DataArea area) => area switch
    {
        DataArea.Coils => 1,               // 0xxxx
        DataArea.DiscreteInputs => 10001,  // 1xxxx
        DataArea.InputRegisters => 30001,  // 3xxxx
        DataArea.HoldingRegisters => 40001,// 4xxxx
        _ => 1
    };

    public static string StyleName(AddressStyle style) => style switch
    {
        AddressStyle.ProtocolZeroBased => "协议地址 (0 基)",
        AddressStyle.DocumentOneBased => "文档地址 (1 基)",
        _ => "Modicon (4xxxx)"
    };
}
