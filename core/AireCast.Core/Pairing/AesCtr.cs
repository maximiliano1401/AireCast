// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;

namespace AireCast.Core.Pairing;

/// <summary>
/// AES-CTR de flujo: cada llamada sigue donde terminó la anterior, a mitad de bloque si hace falta. El
/// flujo se genera por lotes: una llamada a AES por bloque de 16 bytes es demasiado cara en el teléfono.
/// </summary>
public sealed class AesCtr : IDisposable
{
    private const int BlockSize = 16;
    private const int BatchBlocks = 1024;

    private readonly Aes _aes = Aes.Create();
    private readonly byte[] _counter;
    private readonly byte[] _counters = new byte[BlockSize * BatchBlocks];
    private readonly byte[] _keystream = new byte[BlockSize * BatchBlocks];
    private int _used = BlockSize * BatchBlocks;

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
            if (_used == _keystream.Length)
                NextBatch();
            output[i] = (byte)(input[i] ^ _keystream[_used++]);
        }
        return output;
    }

    private void NextBatch()
    {
        for (int block = 0; block < BatchBlocks; block++)
        {
            _counter.CopyTo(_counters, block * BlockSize);
            for (int i = BlockSize - 1; i >= 0 && ++_counter[i] == 0; i--)
            {
            }
        }
        _aes.EncryptEcb(_counters, _keystream, PaddingMode.None);
        _used = 0;
    }

    public void Dispose() => _aes.Dispose();
}
