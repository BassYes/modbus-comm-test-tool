namespace Comm.Protocols.Modbus;

public enum PduKind
{
    ReadBits,
    ReadRegisters,
    WriteAck,
    Exception,
    Unknown
}

/// <summary>解析后的 PDU，用于报文详情与结果表展示。</summary>
public sealed record ParsedPdu(
    PduKind Kind,
    byte Function,
    ushort? Address,
    ushort? Quantity,
    bool[]? Bits,
    ushort[]? Registers,
    byte? ExceptionCode,
    bool IsRequest)
{
    public string Describe()
    {
        var name = Fn.Name(Function);
        switch (Kind)
        {
            case PduKind.Exception:
                return $"{name}：异常码 0x{ExceptionCode:X2} — {ModbusExceptions.Describe(ExceptionCode!.Value)}";
            case PduKind.ReadBits:
                return $"{name}：起始 {Address}，数量 {Quantity}，" +
                       (Bits is null ? "无数据" : $"值 [{string.Join(", ", Bits.Select(b => b ? 1 : 0))}]");
            case PduKind.ReadRegisters:
                return $"{name}：起始 {Address}，数量 {Quantity}，" +
                       (Registers is null ? "无数据" : $"值 [{string.Join(", ", Registers.Select(r => "0x" + r.ToString("X4")))}]");
            case PduKind.WriteAck:
                return $"{name}：地址 {Address}，值/数量 {Quantity}";
            default:
                return name;
        }
    }
}

/// <summary>PDU 解析器（不参与收发，仅用于展示）。</summary>
public static class PduParser
{
    public static ParsedPdu Parse(ModbusPdu pdu, bool isRequest)
    {
        byte fc = pdu.Function;
        var d = pdu.Data;

        if (Fn.IsException(fc))
        {
            var code = d.Length >= 1 ? d[0] : (byte)0;
            return new ParsedPdu(PduKind.Exception, fc, null, null, null, null, code, isRequest);
        }

        switch (fc)
        {
            case Fn.ReadCoils:
            case Fn.ReadDiscreteInputs:
                return isRequest ? ParseReadRequest(pdu) : ParseReadBitsResponse(pdu);
            case Fn.ReadHoldingRegisters:
            case Fn.ReadInputRegisters:
                return isRequest ? ParseReadRequest(pdu) : ParseReadRegistersResponse(pdu);
            case Fn.WriteSingleCoil:
                if (isRequest && d.Length >= 4)
                {
                    ushort addr = (ushort)((d[0] << 8) | d[1]);
                    ushort val = (ushort)((d[2] << 8) | d[3]);
                    return new ParsedPdu(PduKind.WriteAck, fc, addr, val, null, null, null, true);
                }
                return ParseWriteAckResponse(pdu);
            case Fn.WriteSingleRegister:
                if (isRequest && d.Length >= 4)
                {
                    ushort addr = (ushort)((d[0] << 8) | d[1]);
                    ushort val = (ushort)((d[2] << 8) | d[3]);
                    return new ParsedPdu(PduKind.WriteAck, fc, addr, val, null, null, null, true);
                }
                return ParseWriteAckResponse(pdu);
            case Fn.WriteMultipleCoils:
            case Fn.WriteMultipleRegisters:
                if (isRequest && d.Length >= 5)
                {
                    ushort addr = (ushort)((d[0] << 8) | d[1]);
                    ushort qty = (ushort)((d[2] << 8) | d[3]);
                    return new ParsedPdu(PduKind.WriteAck, fc, addr, qty, null, null, null, true);
                }
                return ParseWriteAckResponse(pdu);
            case Fn.MaskWriteRegister:
                if (d.Length >= 6)
                {
                    ushort addr = (ushort)((d[0] << 8) | d[1]);
                    return new ParsedPdu(PduKind.WriteAck, fc, addr, null, null, null, null, isRequest);
                }
                break;
        }

        return new ParsedPdu(PduKind.Unknown, fc, null, null, null, null, null, isRequest);
    }

    private static ParsedPdu ParseReadRequest(ModbusPdu pdu)
    {
        var d = pdu.Data;
        if (d.Length < 4) return new ParsedPdu(PduKind.Unknown, pdu.Function, null, null, null, null, null, true);
        ushort addr = (ushort)((d[0] << 8) | d[1]);
        ushort qty = (ushort)((d[2] << 8) | d[3]);
        return new ParsedPdu(PduKind.ReadRegisters, pdu.Function, addr, qty, null, null, null, true);
    }

    private static ParsedPdu ParseReadBitsResponse(ModbusPdu pdu)
    {
        var d = pdu.Data;
        if (d.Length < 2) return new ParsedPdu(PduKind.Unknown, pdu.Function, null, null, null, null, null, false);
        int byteCount = d[0];
        var bits = new bool[byteCount * 8];
        for (int i = 0; i < byteCount && i + 1 < d.Length; i++)
        {
            for (int b = 0; b < 8; b++) bits[i * 8 + b] = (d[i + 1] & (1 << b)) != 0;
        }
        return new ParsedPdu(PduKind.ReadBits, pdu.Function, null, (ushort)(byteCount * 8), bits, null, null, false);
    }

    private static ParsedPdu ParseReadRegistersResponse(ModbusPdu pdu)
    {
        var d = pdu.Data;
        if (d.Length < 1) return new ParsedPdu(PduKind.Unknown, pdu.Function, null, null, null, null, null, false);
        int byteCount = d[0];
        int count = byteCount / 2;
        var regs = new ushort[count];
        for (int i = 0; i < count && 1 + i * 2 + 1 < d.Length; i++)
        {
            regs[i] = (ushort)((d[1 + i * 2] << 8) | d[2 + i * 2]);
        }
        return new ParsedPdu(PduKind.ReadRegisters, pdu.Function, null, (ushort)count, null, regs, null, false);
    }

    private static ParsedPdu ParseWriteAckResponse(ModbusPdu pdu)
    {
        var d = pdu.Data;
        if (d.Length < 4) return new ParsedPdu(PduKind.Unknown, pdu.Function, null, null, null, null, null, false);
        ushort addr = (ushort)((d[0] << 8) | d[1]);
        ushort val = (ushort)((d[2] << 8) | d[3]);
        return new ParsedPdu(PduKind.WriteAck, pdu.Function, addr, val, null, null, null, false);
    }

    /// <summary>位区响应按请求数量裁剪（响应按字节填充，末尾为填充位）。</summary>
    public static bool[] TrimBits(bool[] bits, int quantity) =>
        quantity > 0 && quantity <= bits.Length ? bits[..quantity] : bits;
}
