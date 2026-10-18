using System.Globalization;
using System.Text;

namespace Comm.Scanning;

/// <summary>
/// 数据解码（纯函数，无 I/O，见规划 14.6.2）。
/// 约定：寄存器的原始字节序列是“线上顺序”；<see cref="ByteOrder"/> 描述它与“数值大端字节序”的对应关系。
/// 解码时始终同时提供原始寄存器值与解释结果，避免解析结果掩盖原始数据。
/// </summary>
public static class DataDecoder
{
    public static string Decode(ReadOnlySpan<ushort> registers, DataType type, ByteOrder order,
        double scale = 1.0, double offset = 0.0)
    {
        if (registers.Length == 0) return string.Empty;

        switch (type)
        {
            case DataType.Bit:
                return (registers[0] & 0x0001) != 0 ? "1 (ON)" : "0 (OFF)";

            case DataType.Hex:
                return "0x" + registers[0].ToString("X4");

            case DataType.Binary:
                return Convert.ToString(registers[0], 2).PadLeft(16, '0');

            case DataType.Bcd:
                return DecodeBcd(registers[0]);

            case DataType.Ascii:
            {
                var sb = new StringBuilder(registers.Length * 2);
                foreach (var register in registers)
                {
                    sb.Append((char)(register >> 8));
                    sb.Append((char)(register & 0xFF));
                }
                var text = sb.ToString().TrimEnd('\0', ' ');
                return text.Length == 0 ? "(空文本)" : Sanitize(text);
            }

            case DataType.UInt16:
                return Apply(registers[0], scale, offset);

            case DataType.Int16:
                return Apply(unchecked((short)registers[0]), scale, offset);
        }

        // 多寄存器类型：先还原为数值的大端字节序列，再按 Little-Endian 交给 BitConverter。
        var bigEndian = ToBigEndianBytes(registers, type.RegisterCount(), order);
        var littleEndian = new byte[bigEndian.Length];
        Array.Copy(bigEndian, littleEndian, bigEndian.Length);
        Array.Reverse(littleEndian);

        switch (type)
        {
            case DataType.UInt32:
                return Apply(BitConverter.ToUInt32(littleEndian, 0), scale, offset);
            case DataType.Int32:
                return Apply(BitConverter.ToInt32(littleEndian, 0), scale, offset);
            case DataType.UInt64:
                return Apply(BitConverter.ToUInt64(littleEndian, 0), scale, offset);
            case DataType.Int64:
                return Apply(BitConverter.ToInt64(littleEndian, 0), scale, offset);
            case DataType.Float32:
                return FormatFloat(BitConverter.ToSingle(littleEndian, 0), scale, offset);
            case DataType.Float64:
                return FormatFloat(BitConverter.ToDouble(littleEndian, 0), scale, offset);
            default:
                return string.Empty;
        }
    }

    /// <summary>尝试取数值结果，用于寄存器定位（见规划 4.4）。</summary>
    public static bool TryDecodeNumber(ReadOnlySpan<ushort> registers, DataType type, ByteOrder order,
        double scale, double offset, out double value)
    {
        value = 0;
        if (registers.Length < type.RegisterCount()) return false;

        try
        {
            switch (type)
            {
                case DataType.UInt16:
                    value = registers[0];
                    break;
                case DataType.Int16:
                    value = unchecked((short)registers[0]);
                    break;
                case DataType.Bcd:
                    value = DecodeBcdValue(registers[0]);
                    if (double.IsNaN(value)) return false;
                    break;
                case DataType.Bit:
                    value = registers[0] & 1;
                    break;
                default:
                {
                    var bigEndian = ToBigEndianBytes(registers, type.RegisterCount(), order);
                    var littleEndian = new byte[bigEndian.Length];
                    Array.Copy(bigEndian, littleEndian, bigEndian.Length);
                    Array.Reverse(littleEndian);

                    switch (type)
                    {
                        case DataType.UInt32:
                            value = BitConverter.ToUInt32(littleEndian, 0);
                            break;
                        case DataType.Int32:
                            value = BitConverter.ToInt32(littleEndian, 0);
                            break;
                        case DataType.Float32:
                        {
                            float f = BitConverter.ToSingle(littleEndian, 0);
                            if (float.IsNaN(f) || float.IsInfinity(f)) return false;
                            value = f;
                            break;
                        }
                        case DataType.Float64:
                        {
                            double d = BitConverter.ToDouble(littleEndian, 0);
                            if (double.IsNaN(d) || double.IsInfinity(d)) return false;
                            value = d;
                            break;
                        }
                        case DataType.UInt64:
                        {
                            ulong raw = BitConverter.ToUInt64(littleEndian, 0);
                            if (raw > long.MaxValue) return false;
                            value = raw;
                            break;
                        }
                        case DataType.Int64:
                            value = BitConverter.ToInt64(littleEndian, 0);
                            break;
                        default:
                            return false;
                    }
                    break;
                }
            }

            value = value * scale + offset;
            return double.IsFinite(value);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>把寄存器原始字节序列还原为「数值的大端字节序」。</summary>
    public static byte[] ToBigEndianBytes(ReadOnlySpan<ushort> registers, int count, ByteOrder order)
    {
        int n = Math.Min(count, registers.Length);
        var wire = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            wire[i * 2] = (byte)(registers[i] >> 8);
            wire[i * 2 + 1] = (byte)(registers[i] & 0xFF);
        }

        return ApplyOrder(wire, n, order);
    }

    /// <summary>
    /// 字节序 / 字序重排。四种组合（ABCD / BADC / CDAB / DCBA）都是自逆置换，
    /// 因此读（线上 → 数值）与写（数值 → 线上）可以共用同一实现。
    /// </summary>
    private static byte[] ApplyOrder(byte[] source, int registerCount, ByteOrder order)
    {
        if (registerCount <= 1 || order == ByteOrder.Abcd) return source.ToArray();

        var result = new byte[source.Length];
        switch (order)
        {
            case ByteOrder.Badc:
                for (int i = 0; i < registerCount; i++)
                {
                    result[i * 2] = source[i * 2 + 1];
                    result[i * 2 + 1] = source[i * 2];
                }
                break;

            case ByteOrder.Cdab:
                for (int i = 0; i + 1 < registerCount; i += 2)
                {
                    Array.Copy(source, (i + 1) * 2, result, i * 2, 2);
                    Array.Copy(source, i * 2, result, (i + 1) * 2, 2);
                }
                if (registerCount % 2 != 0)
                {
                    Array.Copy(source, (registerCount - 1) * 2, result, (registerCount - 1) * 2, 2);
                }
                break;

            case ByteOrder.Dcba:
                for (int i = 0; i < source.Length; i++) result[i] = source[source.Length - 1 - i];
                break;
        }

        return result;
    }

    /// <summary>把数值编码为寄存器序列（写操作与测试用）。</summary>
    public static ushort[] Encode(double value, DataType type, ByteOrder order, double scale = 1.0, double offset = 0.0)
    {
        double raw = scale == 0 ? value : (value - offset) / scale;

        switch (type)
        {
            case DataType.UInt16:
                return new[] { (ushort)Math.Clamp(raw, 0, ushort.MaxValue) };
            case DataType.Int16:
                return new[] { unchecked((ushort)(short)raw) };
            case DataType.UInt32:
            case DataType.Int32:
            case DataType.Float32:
            {
                var bytes = type switch
                {
                    DataType.UInt32 => BitConverter.GetBytes((uint)raw),
                    DataType.Int32 => BitConverter.GetBytes((int)raw),
                    _ => BitConverter.GetBytes((float)raw)
                };
                return FromValueBytes(bytes, order);
            }
            case DataType.UInt64:
            case DataType.Int64:
            case DataType.Float64:
            {
                var bytes = type switch
                {
                    DataType.UInt64 => BitConverter.GetBytes((ulong)raw),
                    DataType.Int64 => BitConverter.GetBytes((long)raw),
                    _ => BitConverter.GetBytes(raw)
                };
                return FromValueBytes(bytes, order);
            }
            default:
                return new[] { (ushort)Math.Clamp(raw, 0, ushort.MaxValue) };
        }
    }

    /// <summary>数值（本机 Little-Endian 字节）→ 寄存器序列。</summary>
    private static ushort[] FromValueBytes(byte[] valueBytes, ByteOrder order)
    {
        var bigEndian = new byte[valueBytes.Length];
        Array.Copy(valueBytes, bigEndian, valueBytes.Length);
        Array.Reverse(bigEndian);

        if (bigEndian.Length % 2 != 0) throw new ArgumentException("字节数必须是偶数");

        int registerCount = bigEndian.Length / 2;
        var wire = ApplyOrder(bigEndian, registerCount, order);

        var registers = new ushort[registerCount];
        for (int i = 0; i < registerCount; i++)
        {
            registers[i] = (ushort)((wire[i * 2] << 8) | wire[i * 2 + 1]);
        }
        return registers;
    }

    private static string Apply(double value, double scale, double offset) =>
        ((value * scale) + offset).ToString("F0", CultureInfo.InvariantCulture);

    private static string FormatFloat(double value, double scale, double offset)
    {
        if (double.IsNaN(value)) return "NaN（非数值，可能是字节序错误或未初始化）";
        if (double.IsInfinity(value)) return value > 0 ? "+Inf" : "-Inf";

        double scaled = (value * scale) + offset;
        return scaled == Math.Floor(scaled) && Math.Abs(scaled) < 1e15
            ? scaled.ToString("F0", CultureInfo.InvariantCulture)
            : scaled.ToString("G9", CultureInfo.InvariantCulture);
    }

    private static string DecodeBcd(ushort value)
    {
        var text = new StringBuilder(4);
        for (int shift = 12; shift >= 0; shift -= 4)
        {
            int nibble = (value >> shift) & 0x0F;
            if (nibble > 9) return $"非法 BCD（0x{value:X4}）";
            text.Append((char)('0' + nibble));
        }
        return text.ToString();
    }

    private static double DecodeBcdValue(ushort value)
    {
        double result = 0;
        for (int shift = 12; shift >= 0; shift -= 4)
        {
            int nibble = (value >> shift) & 0x0F;
            if (nibble > 9) return double.NaN;
            result = result * 10 + nibble;
        }
        return result;
    }

    private static string Sanitize(string text) =>
        new(text.Select(c => c is >= (char)0x20 and < (char)0x7F ? c : '.').ToArray());
}
