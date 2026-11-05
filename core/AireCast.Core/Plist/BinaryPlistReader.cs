// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace AireCast.Core.Plist;

/// <summary>
/// Lee plist binarios (bplist00). Los valores salen como Dictionary&lt;string, object?&gt;,
/// List&lt;object?&gt;, string, long, double, bool, byte[] o DateTime.
/// </summary>
public sealed class BinaryPlistReader
{
    private const int TrailerLength = 32;
    private const int MaxDepth = 64;
    private static readonly DateTime Epoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly byte[] _data;
    private readonly int _offsetSize;
    private readonly int _refSize;
    private readonly int _offsetTable;
    private readonly long _objectCount;

    private BinaryPlistReader(byte[] data, int offsetSize, int refSize, long objectCount, int offsetTable)
    {
        _data = data;
        _offsetSize = offsetSize;
        _refSize = refSize;
        _objectCount = objectCount;
        _offsetTable = offsetTable;
    }

    public static bool IsBinaryPlist(ReadOnlySpan<byte> data) => data.StartsWith("bplist00"u8);

    /// <exception cref="FormatException">No es un plist binario válido.</exception>
    public static object? Read(byte[] data)
    {
        if (!IsBinaryPlist(data) || data.Length < 8 + TrailerLength)
            throw new FormatException("No es un plist binario");
        ReadOnlySpan<byte> trailer = data.AsSpan(data.Length - TrailerLength);
        int offsetSize = trailer[6], refSize = trailer[7];
        long objectCount = BinaryPrimitives.ReadInt64BigEndian(trailer[8..]);
        long top = BinaryPrimitives.ReadInt64BigEndian(trailer[16..]);
        long offsetTable = BinaryPrimitives.ReadInt64BigEndian(trailer[24..]);
        if (offsetSize is < 1 or > 8 || refSize is < 1 or > 8 || objectCount <= 0 || top < 0 || top >= objectCount
            || offsetTable < 8 || offsetTable + objectCount * offsetSize > data.Length - TrailerLength)
            throw new FormatException("Plist binario con cola no válida");

        var reader = new BinaryPlistReader(data, offsetSize, refSize, objectCount, (int)offsetTable);
        return reader.ReadObject(top, 0);
    }

    private object? ReadObject(long index, int depth)
    {
        if (index < 0 || index >= _objectCount || depth > MaxDepth)
            throw new FormatException("Referencia de plist no válida");
        long offset = ReadUInt(_offsetTable + (int)index * _offsetSize, _offsetSize);
        if (offset < 8 || offset >= _offsetTable)
            throw new FormatException("Posición de plist no válida");
        int pos = (int)offset;
        byte marker = _data[pos++];
        int type = marker >> 4, info = marker & 0x0F;

        switch (type)
        {
            case 0x0:
                return info switch { 0x8 => false, 0x9 => true, _ => null };
            case 0x1:
                return ReadInteger(pos, 1 << info);
            case 0x2:
                return ReadReal(pos, 1 << info);
            case 0x3:
                return Epoch.AddSeconds(ReadReal(pos, 8));
            case 0x4:
                return Slice(ref pos, ReadLength(ref pos, info)).ToArray();
            case 0x5:
                return Encoding.ASCII.GetString(Slice(ref pos, ReadLength(ref pos, info)));
            case 0x6:
                return Encoding.BigEndianUnicode.GetString(Slice(ref pos, ReadLength(ref pos, info) * 2));
            case 0x8:
                return ReadInteger(pos, info + 1);
            case 0xA:
            {
                int count = ReadLength(ref pos, info);
                var list = new List<object?>(count);
                for (int i = 0; i < count; i++)
                    list.Add(ReadObject(ReadRef(pos, i), depth + 1));
                return list;
            }
            case 0xD:
            {
                int count = ReadLength(ref pos, info);
                var dict = new Dictionary<string, object?>(count);
                for (int i = 0; i < count; i++)
                {
                    if (ReadObject(ReadRef(pos, i), depth + 1) is not string key)
                        throw new FormatException("Clave de plist que no es texto");
                    dict[key] = ReadObject(ReadRef(pos, count + i), depth + 1);
                }
                return dict;
            }
            default:
                throw new FormatException($"Tipo de plist no soportado: {marker:X2}");
        }
    }

    private int ReadLength(ref int pos, int info)
    {
        if (info != 0xF)
            return info;
        byte marker = _data[pos++];
        if (marker >> 4 != 0x1)
            throw new FormatException("Longitud de plist no válida");
        int size = 1 << (marker & 0x0F);
        long length = ReadInteger(pos, size);
        pos += size;
        if (length < 0 || length > _data.Length)
            throw new FormatException("Longitud de plist fuera de rango");
        return (int)length;
    }

    private ReadOnlySpan<byte> Slice(ref int pos, int length)
    {
        if (pos + length > _data.Length)
            throw new FormatException("Datos de plist incompletos");
        var slice = _data.AsSpan(pos, length);
        pos += length;
        return slice;
    }

    private long ReadRef(int pos, int index) => ReadUInt(pos + index * _refSize, _refSize);

    private long ReadUInt(int pos, int size)
    {
        if (pos + size > _data.Length)
            throw new FormatException("Datos de plist incompletos");
        long value = 0;
        for (int i = 0; i < size; i++)
            value = value << 8 | _data[pos + i];
        return value;
    }

    private long ReadInteger(int pos, int size) => size switch
    {
        <= 8 => ReadUInt(pos, size),
        16 => ReadUInt(pos + 8, 8), // los enteros de 128 bits guardan el valor en la mitad baja
        _ => throw new FormatException("Entero de plist no soportado"),
    };

    private double ReadReal(int pos, int size) => size switch
    {
        4 => BinaryPrimitives.ReadSingleBigEndian(_data.AsSpan(pos, 4)),
        8 => BinaryPrimitives.ReadDoubleBigEndian(_data.AsSpan(pos, 8)),
        _ => throw new FormatException("Número de plist no soportado"),
    };
}
