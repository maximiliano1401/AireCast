// SPDX-License-Identifier: GPL-3.0-or-later
namespace AireCast.Core.Pairing;

/// <summary>Formato TLV8 de HomeKit: tipo, longitud y valor; los valores largos van en trozos de 255.</summary>
public static class Tlv8
{
    public const byte Identifier = 1;
    public const byte Salt = 2;
    public const byte PublicKey = 3;
    public const byte Proof = 4;
    public const byte EncryptedData = 5;
    public const byte SeqNo = 6;
    public const byte Error = 7;
    public const byte Signature = 10;
    public const byte Flags = 0x13;

    public static byte[] Write(params (byte Tag, byte[] Value)[] items)
    {
        using var output = new MemoryStream();
        foreach (var (tag, value) in items)
        {
            for (int pos = 0; pos < value.Length; pos += 255)
            {
                int size = Math.Min(255, value.Length - pos);
                output.WriteByte(tag);
                output.WriteByte((byte)size);
                output.Write(value, pos, size);
            }
        }
        return output.ToArray();
    }

    /// <exception cref="FormatException">Los datos están incompletos.</exception>
    public static Dictionary<byte, byte[]> Read(ReadOnlySpan<byte> data)
    {
        var values = new Dictionary<byte, byte[]>();
        int pos = 0;
        while (pos < data.Length)
        {
            if (pos + 2 > data.Length || pos + 2 + data[pos + 1] > data.Length)
                throw new FormatException("TLV8 incompleto");
            byte tag = data[pos];
            byte[] value = data.Slice(pos + 2, data[pos + 1]).ToArray();
            pos += 2 + value.Length;
            values[tag] = values.TryGetValue(tag, out byte[]? previous) ? [.. previous, .. value] : value;
        }
        return values;
    }
}
