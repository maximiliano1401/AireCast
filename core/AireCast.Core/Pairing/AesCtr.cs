// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;

namespace AireCast.Core.Pairing;

/// <summary>AES-CTR de flujo: cada llamada sigue donde terminó la anterior, a mitad de bloque si hace falta.</summary>
public sealed class AesCtr : IDisposable
{
    private const int BlockSize = 16;

    private readonly Aes _aes = Aes.Create();
    private readonly byte[] _counter;
    private readonly byte[] _keystream = new byte[BlockSize];
    private int _used = BlockSize;

    public AesCtr(byte[] key, byte[] iv)
    {
        _aes.Key = key;
        _counter = (byte[])iv.Clone();
    }

    public byte[] Transform(ReadOnlySpan<byte> input)
    {
        byte[] output = new byte[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            if (_used == BlockSize)
                NextBlock();
            output[i] = (byte)(input[i] ^ _keystream[_used++]);
        }
        return output;
    }

    private void NextBlock()
    {
        _aes.EncryptEcb(_counter, _keystream, PaddingMode.None);
        _used = 0;
        for (int i = BlockSize - 1; i >= 0 && ++_counter[i] == 0; i--)
        {
        }
    }

    public void Dispose() => _aes.Dispose();
}
