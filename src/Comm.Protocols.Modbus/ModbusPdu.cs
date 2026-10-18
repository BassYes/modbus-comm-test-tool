namespace Comm.Protocols.Modbus;

/// <summary>协议数据单元：功能码 + 数据。</summary>
public sealed record ModbusPdu(byte Function, byte[] Data)
{
    public byte[] ToBytes()
    {
        var bytes = new byte[Data.Length + 1];
        bytes[0] = Function;
        Data.CopyTo(bytes, 1);
        return bytes;
    }

    public static ModbusPdu Parse(ReadOnlySpan<byte> pduBytes)
    {
        if (pduBytes.Length < 1) throw new ArgumentException("PDU 长度不足", nameof(pduBytes));
        return new ModbusPdu(pduBytes[0], pduBytes[1..].ToArray());
    }

    public static ModbusPdu Raw(byte function, params byte[] data) => new(function, data);
}

/// <summary>请求构建器。</summary>
public static class ModbusRequests
{
    public static ModbusPdu Read(DataArea area, ushort address, ushort quantity) =>
        new(area.ReadFunctionCode(), new[]
        {
            (byte)(address >> 8), (byte)(address & 0xFF),
            (byte)(quantity >> 8), (byte)(quantity & 0xFF)
        });

    public static ModbusPdu WriteSingleCoil(ushort address, bool value) =>
        new(Fn.WriteSingleCoil, new[]
        {
            (byte)(address >> 8), (byte)(address & 0xFF),
            (byte)(value ? 0xFF : 0x00), (byte)0x00
        });

    public static ModbusPdu WriteSingleRegister(ushort address, ushort value) =>
        new(Fn.WriteSingleRegister, new[]
        {
            (byte)(address >> 8), (byte)(address & 0xFF),
            (byte)(value >> 8), (byte)(value & 0xFF)
        });

    public static ModbusPdu WriteMultipleRegisters(ushort address, IReadOnlyList<ushort> values)
    {
        var data = new byte[5 + values.Count * 2];
        data[0] = (byte)(address >> 8);
        data[1] = (byte)(address & 0xFF);
        data[2] = (byte)(values.Count >> 8);
        data[3] = (byte)(values.Count & 0xFF);
        data[4] = (byte)(values.Count * 2);
        for (int i = 0; i < values.Count; i++)
        {
            data[5 + i * 2] = (byte)(values[i] >> 8);
            data[6 + i * 2] = (byte)(values[i] & 0xFF);
        }
        return new ModbusPdu(Fn.WriteMultipleRegisters, data);
    }

    public static ModbusPdu WriteMultipleCoils(ushort address, IReadOnlyList<bool> values)
    {
        int byteCount = (values.Count + 7) / 8;
        var data = new byte[5 + byteCount];
        data[0] = (byte)(address >> 8);
        data[1] = (byte)(address & 0xFF);
        data[2] = (byte)(values.Count >> 8);
        data[3] = (byte)(values.Count & 0xFF);
        data[4] = (byte)byteCount;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i]) data[5 + i / 8] |= (byte)(1 << (i % 8));
        }
        return new ModbusPdu(Fn.WriteMultipleCoils, data);
    }
}
