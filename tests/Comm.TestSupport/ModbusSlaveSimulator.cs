using Comm.Core.Codecs;
using Comm.Protocols.Modbus;

namespace Comm.TestSupport;

/// <summary>
/// 测试用 Modbus 从站模拟器（不属于产品功能，见规划 16.3.3）。
/// 支持寄存器映射、异常响应、离线、CRC 破坏与 RS-485 回显模拟。
/// </summary>
public sealed class ModbusSlaveSimulator
{
    public byte UnitId { get; set; } = 1;
    public Dictionary<ushort, ushort> HoldingRegisters { get; } = new();
    public Dictionary<ushort, ushort> InputRegisters { get; } = new();
    public Dictionary<ushort, bool> Coils { get; } = new();

    /// <summary>命中这些地址时返回异常码 02（非法数据地址）。</summary>
    public HashSet<ushort> IllegalAddresses { get; } = new();

    /// <summary>不支持的功能码返回异常码 01。</summary>
    public HashSet<byte> UnsupportedFunctions { get; } = new();

    public int ResponseDelayMs { get; set; }
    public bool Offline { get; set; }

    /// <summary>破坏响应中的 CRC，用于校验错误路径测试。</summary>
    public bool CorruptChecksum { get; set; }

    /// <summary>模拟 RS-485 半双工适配器回显：把请求原样发回后再发响应。</summary>
    public bool EchoRequest { get; set; }

    /// <summary>记录收到的所有请求 PDU，用于断言“扫描不产生写操作”。</summary>
    public List<ModbusPdu> ReceivedRequests { get; } = new();

    /// <summary>自定义拦截：返回 null 表示按默认规则处理，返回 PDU 则直接使用。</summary>
    public Func<byte, ModbusPdu, ModbusPdu?>? Interceptor { get; set; }

    private static readonly ModbusPdu BroadcastMarker = new(0x00, Array.Empty<byte>());

    /// <summary>处理一次请求。广播（地址 0）时执行写操作但返回 null（不响应）。</summary>
    public ModbusPdu? Handle(byte unitId, ModbusPdu request)
    {
        var result = HandleCore(unitId, request);
        if (unitId == 0) return null;
        return ReferenceEquals(result, BroadcastMarker) ? null : result;
    }

    private ModbusPdu? HandleCore(byte unitId, ModbusPdu request)
    {
        ReceivedRequests.Add(request);

        if (Offline) return null;

        bool broadcast = unitId == 0;
        if (!broadcast && unitId != UnitId) return null;
        if (broadcast && !Fn.IsWrite(request.Function)) return null;

        if (Interceptor is not null)
        {
            var custom = Interceptor(unitId, request);
            if (custom is not null) return custom;
        }

        if (UnsupportedFunctions.Contains(request.Function))
            return Exception(request.Function, 0x01);

        var data = request.Data;
        switch (request.Function)
        {
            case Fn.ReadCoils:
            case Fn.ReadDiscreteInputs:
                return ReadBits(request.Function, data);

            case Fn.ReadHoldingRegisters:
                return ReadRegisters(request.Function, data, HoldingRegisters);

            case Fn.ReadInputRegisters:
                return ReadRegisters(request.Function, data, InputRegisters);

            case Fn.WriteSingleCoil:
            {
                if (data.Length < 4) return Exception(request.Function, 0x03);
                ushort address = ReadUInt16(data, 0);
                if (IllegalAddresses.Contains(address)) return Exception(request.Function, 0x02);
                Coils[address] = data[2] == 0xFF;
                return broadcast ? BroadcastMarker : new ModbusPdu(request.Function, data[..4]);
            }

            case Fn.WriteSingleRegister:
            {
                if (data.Length < 4) return Exception(request.Function, 0x03);
                ushort address = ReadUInt16(data, 0);
                if (IllegalAddresses.Contains(address)) return Exception(request.Function, 0x02);
                HoldingRegisters[address] = ReadUInt16(data, 2);
                return broadcast ? BroadcastMarker : new ModbusPdu(request.Function, data[..4]);
            }

            case Fn.WriteMultipleRegisters:
            {
                if (data.Length < 5) return Exception(request.Function, 0x03);
                ushort address = ReadUInt16(data, 0);
                ushort quantity = ReadUInt16(data, 2);
                for (int i = 0; i < quantity; i++)
                {
                    if (IllegalAddresses.Contains((ushort)(address + i))) return Exception(request.Function, 0x02);
                    if (5 + i * 2 + 1 < data.Length) HoldingRegisters[(ushort)(address + i)] = ReadUInt16(data, 5 + i * 2);
                }
                return broadcast ? BroadcastMarker : new ModbusPdu(request.Function, new[]
                {
                    (byte)(address >> 8), (byte)(address & 0xFF),
                    (byte)(quantity >> 8), (byte)(quantity & 0xFF)
                });
            }

            case Fn.WriteMultipleCoils:
            {
                if (data.Length < 5) return Exception(request.Function, 0x03);
                ushort address = ReadUInt16(data, 0);
                ushort quantity = ReadUInt16(data, 2);
                for (int i = 0; i < quantity; i++)
                {
                    bool value = 5 + i / 8 < data.Length && (data[5 + i / 8] & (1 << (i % 8))) != 0;
                    Coils[(ushort)(address + i)] = value;
                }
                return broadcast ? BroadcastMarker : new ModbusPdu(request.Function, new[]
                {
                    (byte)(address >> 8), (byte)(address & 0xFF),
                    (byte)(quantity >> 8), (byte)(quantity & 0xFF)
                });
            }

            default:
                return Exception(request.Function, 0x01);
        }
    }

    private ModbusPdu ReadBits(byte function, byte[] data)
    {
        if (data.Length < 4) return Exception(function, 0x03);
        ushort address = ReadUInt16(data, 0);
        ushort quantity = ReadUInt16(data, 2);
        if (quantity is 0 or > 2000) return Exception(function, 0x03);

        for (int i = 0; i < quantity; i++)
        {
            if (IllegalAddresses.Contains((ushort)(address + i))) return Exception(function, 0x02);
        }

        int byteCount = (quantity + 7) / 8;
        var result = new byte[1 + byteCount];
        result[0] = (byte)byteCount;
        for (int i = 0; i < quantity; i++)
        {
            if (Coils.TryGetValue((ushort)(address + i), out var value) && value)
                result[1 + i / 8] |= (byte)(1 << (i % 8));
        }
        return new ModbusPdu(function, result);
    }

    private ModbusPdu ReadRegisters(byte function, byte[] data, Dictionary<ushort, ushort> map)
    {
        if (data.Length < 4) return Exception(function, 0x03);
        ushort address = ReadUInt16(data, 0);
        ushort quantity = ReadUInt16(data, 2);
        if (quantity is 0 or > 125) return Exception(function, 0x03);

        for (int i = 0; i < quantity; i++)
        {
            if (IllegalAddresses.Contains((ushort)(address + i))) return Exception(function, 0x02);
        }

        var result = new byte[1 + quantity * 2];
        result[0] = (byte)(quantity * 2);
        for (int i = 0; i < quantity; i++)
        {
            map.TryGetValue((ushort)(address + i), out var value);
            result[1 + i * 2] = (byte)(value >> 8);
            result[2 + i * 2] = (byte)(value & 0xFF);
        }
        return new ModbusPdu(function, result);
    }

    private static ModbusPdu Exception(byte function, byte code) =>
        new((byte)(function | 0x80), new[] { code });

    private static ushort ReadUInt16(byte[] data, int offset) => (ushort)((data[offset] << 8) | data[offset + 1]);

    /// <summary>生成可交给回环通道的响应函数。</summary>
    public Func<byte[], byte[]?> CreateResponder(IFrameCodec codec)
    {
        return request =>
        {
            if (request.Length < 4) return null;

            byte unitId;
            byte[] pduBytes;
            ushort transactionId = 0;

            if (codec.ChecksumLength == 0)
            {
                if (request.Length < 8) return null;
                transactionId = (ushort)((request[0] << 8) | request[1]);
                unitId = request[6];
                pduBytes = request[7..];
            }
            else
            {
                unitId = request[0];
                pduBytes = request[1..^2];
            }

            var response = Handle(unitId, ModbusPdu.Parse(pduBytes));
            if (response is null) return null;

            var adu = codec.BuildAdu(response.ToBytes(), UnitId, transactionId);

            if (CorruptChecksum && codec.ChecksumLength > 0 && adu.Length > 2) adu[^1] ^= 0xFF;

            if (EchoRequest && codec.ChecksumLength > 0)
            {
                var combined = new byte[request.Length + adu.Length];
                request.CopyTo(combined, 0);
                adu.CopyTo(combined, request.Length);
                return combined;
            }

            return adu;
        };
    }
}
