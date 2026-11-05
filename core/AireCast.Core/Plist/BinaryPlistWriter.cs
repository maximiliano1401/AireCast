// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace AireCast.Core.Plist;

/// <summary>Escribe plist binarios (bplist00) con diccionarios, listas, texto, enteros, reales, booleanos y bytes.</summary>
public sealed class BinaryPlistWriter
{
    private const int TrailerLength = 32;

    private readonly List<object?> _objects = [];
    private int _refSize;

    private BinaryPlistWriter()
    {
    }

    public static byte[] Write(object value)
    {
        var writer = new BinaryPlistWriter();
        writer.Collect(value);
        return writer.Serialize();
    }

    /// <summary>Numera cada objeto; los contenedores van antes que su contenido.</summary>
    private void Collect(object? value)
    {
        _objects.Add(value);
        switch (value)
        {
            case IDictionary<string, object?> dict:
                foreach (var (key, item) in dict)
                {
                    Collect(key);
                    Collect(item);
                }
                break;
            case IEnumerable<object?> list:
                foreach (object? item in list)
                    Collect(item);
                break;
        }
    }

    private byte[] Serialize()
    {
        _refSize = _objects.Count <= byte.MaxValue ? 1 : 2;
        using var output = new MemoryStream();
        output.Write("bplist00"u8);

        var offsets = new long[_objects.Count];
        for (int index = 0; index < _objects.Count; index++)
            WriteObject(output, index, offsets);

        long offsetTable = output.Position;
        int offsetSize = SizeFor(offsetTable);
        foreach (long offset in offsets)
            WriteUInt(output, offset, offsetSize);

        Span<byte> trailer = stackalloc byte[TrailerLength];
        trailer.Clear();
        trailer[6] = (byte)offsetSize;
        trailer[7] = (byte)_refSize;
        BinaryPrimitives.WriteInt64BigEndian(trailer[8..], _objects.Count);
        BinaryPrimitives.WriteInt64BigEndian(trailer[24..], offsetTable);
        output.Write(trailer);
        return output.ToArray();
    }

    private void WriteObject(MemoryStream output, int index, long[] offsets)
    {
        object? value = _objects[index];
        offsets[index] = output.Position;
        switch (value)
        {
            case null:
                output.WriteByte(0x00);
                break;
            case bool flag:
                output.WriteByte(flag ? (byte)0x09 : (byte)0x08);
                break;
            case int or long or uint:
                WriteInteger(output, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
            case double real:
                output.WriteByte(0x23);
                Span<byte> bytes = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(bytes, real);
                output.Write(bytes);
                break;
            case byte[] data:
                WriteMarker(output, 0x4, data.Length);
                output.Write(data);
                break;
            case string text:
                WriteText(output, text);
                break;
            case IDictionary<string, object?> dict:
                WriteMarker(output, 0xD, dict.Count);
                // Cada par se numeró como clave, valor (y sus contenidos): se escriben las claves y luego los valores.
                var keys = new List<int>();
                var values = new List<int>();
                int child = index + 1;
                foreach (var (_, item) in dict)
                {
                    keys.Add(child);
                    values.Add(child + 1);
                    child = SkipTree(child + 1);
                }
                foreach (int key in keys.Concat(values))
                    WriteUInt(output, key, _refSize);
                break;
            case IEnumerable<object?> list:
                var items = new List<int>();
                int position = index + 1;
                foreach (object? _ in list)
                {
                    items.Add(position);
                    position = SkipTree(position);
                }
                WriteMarker(output, 0xA, items.Count);
                foreach (int item in items)
                    WriteUInt(output, item, _refSize);
                break;
            default:
                throw new ArgumentException($"Tipo no soportado en un plist: {value.GetType().Name}");
        }
    }

    /// <summary>Índice del objeto que sigue a este y a todo su contenido.</summary>
    private int SkipTree(int index)
    {
        object? value = _objects[index];
        int next = index + 1;
        switch (value)
        {
            case IDictionary<string, object?> dict:
                for (int i = 0; i < dict.Count; i++)
                    next = SkipTree(next + 1);
                break;
            case IEnumerable<object?> list:
                foreach (object? _ in list)
                    next = SkipTree(next);
                break;
        }
        return next;
    }

    private static void WriteText(MemoryStream output, string text)
    {
        if (Ascii.IsValid(text))
        {
            WriteMarker(output, 0x5, text.Length);
            output.Write(Encoding.ASCII.GetBytes(text));
        }
        else
        {
            WriteMarker(output, 0x6, text.Length);
            output.Write(Encoding.BigEndianUnicode.GetBytes(text));
        }
    }

    private static void WriteMarker(MemoryStream output, int type, int length)
    {
        if (length < 0xF)
        {
            output.WriteByte((byte)(type << 4 | length));
            return;
        }
        output.WriteByte((byte)(type << 4 | 0xF));
        WriteInteger(output, length);
    }

    private static void WriteInteger(MemoryStream output, long value)
    {
        int size = value < 0 ? 8 : SizeFor(value) switch { 1 => 1, 2 => 2, <= 4 => 4, _ => 8 };
        output.WriteByte((byte)(0x10 | int.Log2(size)));
        WriteUInt(output, value, size);
    }

    private static void WriteUInt(MemoryStream output, long value, int size)
    {
        for (int shift = (size - 1) * 8; shift >= 0; shift -= 8)
            output.WriteByte((byte)(value >> shift));
    }

    private static int SizeFor(long value)
    {
        int size = 1;
        while (size < 8 && value >> (size * 8) != 0)
            size++;
        return size;
    }
}
