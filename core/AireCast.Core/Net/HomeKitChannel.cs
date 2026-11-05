// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using AireCast.Core.Pairing;

namespace AireCast.Core.Net;

/// <summary>
/// Cifrado de HomeKit para un canal TCP: bloques de hasta 1024 bytes con su longitud (2 bytes,
/// little-endian) en claro como datos autenticados, y un contador por sentido como nonce.
/// </summary>
public sealed class HomeKitChannel(byte[] outputKey, byte[] inputKey)
{
    private const int FrameLength = 1024;
    private const int TagLength = 16;

    private readonly MemoryStream _pending = new();
    private ulong _outputCounter;
    private ulong _inputCounter;

    public byte[] Encrypt(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        for (int pos = 0; pos < data.Length; pos += FrameLength)
        {
            ReadOnlySpan<byte> frame = data.Slice(pos, Math.Min(FrameLength, data.Length - pos));
            byte[] length = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)frame.Length);
            output.Write(length);
            output.Write(PairCrypto.Seal(outputKey, PairCrypto.CounterNonce(_outputCounter++), frame.ToArray(), length));
        }
        return output.ToArray();
    }

    /// <summary>Descifra los bloques completos recibidos; lo incompleto espera a la siguiente llamada.</summary>
    /// <exception cref="PairingException">Un bloque no se autentica.</exception>
    public byte[] Decrypt(ReadOnlySpan<byte> data)
    {
        _pending.Write(data);
        byte[] buffer = _pending.ToArray();
        using var output = new MemoryStream();
        int pos = 0;
        while (pos + 2 <= buffer.Length)
        {
            int block = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(pos)) + TagLength;
            if (pos + 2 + block > buffer.Length)
                break;
            output.Write(PairCrypto.Open(inputKey, PairCrypto.CounterNonce(_inputCounter++),
                buffer[(pos + 2)..(pos + 2 + block)], buffer[pos..(pos + 2)]));
            pos += 2 + block;
        }
        _pending.SetLength(0);
        _pending.Write(buffer, pos, buffer.Length - pos);
        return output.ToArray();
    }
}
