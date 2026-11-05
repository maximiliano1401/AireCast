// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Buffers.Binary;
using System.Numerics;

namespace AireCast.Core.FairPlay;

/// <summary>Hash SAP, MD5 modificado y red white-box del intercambio (m3), traducidos de fairplay.py.</summary>
internal static class SapExchange
{
    internal enum Mutation
    {
        Swap,
        Cycle,
        Kdf,
    }

    private const int BlockLength = 64;
    private const int MaterialLength = 320;

    private static readonly int[] Shifts =
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    ];

    private static readonly uint[] Constants = [0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501, 0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821, 0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8, 0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a, 0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70, 0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665, 0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1, 0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391];

    private static readonly byte[] Hash0 =
    [
        0x96, 0x5f, 0xc6, 0x53, 0xf8, 0x46, 0xcc, 0x18, 0xdf, 0xbe,
        0xb2, 0xf8, 0x38, 0x62, 0xec, 0x22, 0x93, 0xd1, 0x20, 0x8f,
    ];

    private static readonly byte[] Matrix0 =
    [
        0x43, 0x54, 0x62, 0x7a, 0x18, 0xc3, 0xd6, 0xb3, 0x9a, 0x56,
        0xf6, 0x1c, 0x14, 0x3f, 0x0c, 0x1d, 0x3b, 0x36, 0x83, 0xb1,
        0x39, 0x51, 0x4a, 0xaa, 0x09, 0x3e, 0xfe, 0x44, 0xaf, 0xde,
        0xc3, 0x20, 0x9d, 0x42, 0xb8,
    ];

    private static readonly byte[] InitialSessionKey = Convert.FromHexString("dcdcf3b90b74dcfb867ff76016729051");
    private static readonly byte[] KdfPrefix = Convert.FromHexString("fa9cad4d4b68268c7ff38899de922e951e");
    private static readonly byte[] KdfSuffix = Convert.FromHexString("ec4e275efdf2e83097ae70fbe0003f1c39");
    private static readonly byte[] DescriptorPrefix = Convert.FromHexString("a0449c4d09e4bd7f6ec5d0cc359da7467a");
    private static readonly byte[] DescriptorSuffix = Convert.FromHexString("97b50f84e2155a9c24991cf43a09635547");
    private static readonly byte[] FixedBlock = Convert.FromHexString("afc22ba049effcfbfe67ac5ebef6fbcb");
    private static readonly byte[] MaskSuffix = Convert.FromHexString("57d8eecbdefbcf591c27a2cfbeb089");
    private static readonly int[] FirstPositions = [0, 5, 10, 15, 4, 9, 14, 3, 8, 13, 2, 7, 12, 1, 6, 11];
    private static readonly int[] SecondPositions = [0, 13, 10, 7, 4, 1, 14, 11, 8, 5, 2, 15, 12, 9, 6, 3];

    private static void Mutate(uint[] message, uint a, uint b, uint c, uint d, Mutation mutation)
    {
        void Swap(uint i, uint j) => (message[i], message[j]) = (message[j], message[i]);

        if (mutation == Mutation.Kdf)
        {
            Swap(a & 15, b & 15);
            Swap(c & 15, d & 15);
            foreach (int shift in (int[])[4, 8, 12])
                Swap(a >> shift & 15, b >> shift & 15);
            return;
        }

        uint[] index = [a & 15, b & 15, c & 15, d & 15, a >> 4 & 15, b >> 4 & 15, c >> 4 & 15, d >> 4 & 15];
        if (mutation == Mutation.Swap)
        {
            for (uint i = 0; i < index.Length; i++)
                Swap(i, index[i]);
            return;
        }
        uint first = message[index[0]];
        for (int i = 0; i < index.Length - 1; i++)
            message[index[i]] = message[index[i + 1]];
        message[index[^1]] = first;
    }

    internal static uint[] Md5Compress(uint[] state, ReadOnlySpan<byte> block, Mutation mutation)
    {
        uint[] message = new uint[16];
        for (int i = 0; i < 16; i++)
            message[i] = BinaryPrimitives.ReadUInt32BigEndian(block[(i * 4)..]);
        uint a = state[0], b = state[1], c = state[2], d = state[3];
        for (int round = 0; round < 64; round++)
        {
            uint f;
            int word;
            if (round < 16)
                (f, word) = ((b & c) | (~b & d), round);
            else if (round < 32)
                (f, word) = ((d & b) | (~d & c), (5 * round + 1) & 15);
            else if (round < 48)
                (f, word) = (b ^ c ^ d, (3 * round + 5) & 15);
            else
                (f, word) = (c ^ (b | ~d), (7 * round) & 15);
            (a, b, c, d) = (d, b + BitOperations.RotateLeft(a + f + Constants[round] + message[word], Shifts[round]), b, c);
            if (round == 31)
                Mutate(message, a, b, c, d, mutation);
        }
        return [state[0] + a, state[1] + b, state[2] + c, state[3] + d];
    }

    internal static uint[] WordsLittleEndian(ReadOnlySpan<byte> data) =>
    [
        BinaryPrimitives.ReadUInt32LittleEndian(data),
        BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
        BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
        BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
    ];

    private static byte[] BytesBigEndian(uint[] words)
    {
        byte[] output = new byte[words.Length * 4];
        for (int i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(i * 4), words[i]);
        return output;
    }

    /// <summary>Hash SAP propietario de FairPlay sobre un bloque de 64 bytes: 16 bytes.</summary>
    internal static byte[] SapHash(ReadOnlySpan<byte> block)
    {
        int[] hash = Array.ConvertAll(Hash0, v => (int)v);
        int[] matrix = Array.ConvertAll(Matrix0, v => (int)v);
        int[] aux = new int[10];
        int[] work = new int[210];
        for (int i = 0; i < work.Length; i++)
            work[i] = block[(i & 63) ^ 3];

        for (int i = 0; i < 840; i++)
        {
            int x = work[(int)((uint)(i - 155) % 210)];
            int y = work[(int)((uint)(i - 57) % 210)];
            int z = work[(int)((uint)(i - 13) % 210)];
            int w = work[i % 210];
            work[i % 210] = (SapCircuit.Rotl8(y, 5) + (SapCircuit.Rotl8(z, 3) ^ w) - SapCircuit.Rotl8(x, 7)) & 0xFF;
        }

        SapCircuit.Run(hash, matrix, aux, work);

        int[] output = new int[16];
        Array.Copy(aux, 0, output, 0, 3);
        Array.Copy(aux, 3, output, 4, 7);
        for (int i = 0; i < 16; i++)
            output[i] = (output[i] + 0xe1) & 0xFF;
        output[3] = 0x3d;
        output[11] = 0x3c;
        output[10] ^= aux[3] ^ 133;

        for (int i = 0; i < work.Length; i++)
        {
            int value = work[i];
            if (i < matrix.Length)
                value ^= matrix[i];
            if (i < hash.Length)
                value ^= hash[i];
            output[i & 15] ^= value;
        }

        for (int i = 0; i < 256; i++)
        {
            output[i & 15] ^= SapCircuit.Rotl8(output[(i - 7) & 15], 1) ^ SapCircuit.Rotl8(output[(i - 5) & 15], 6)
                ^ SapCircuit.Rotl8(output[(i - 1) & 15], 5);
        }
        return Array.ConvertAll(output, v => (byte)v);
    }

    private static byte[] Md5Padded(byte[] data, int total)
    {
        byte[] padded = new byte[total];
        data.CopyTo(padded, 0);
        padded[data.Length] = 0x80;
        BinaryPrimitives.WriteInt64LittleEndian(padded.AsSpan(total - 8), (long)data.Length * 8);
        return padded;
    }

    internal static byte[] Descriptor(byte[] m3Sap, byte[] m2Sap)
    {
        byte[] padded = Md5Padded([.. DescriptorPrefix, .. m3Sap, .. m2Sap, .. DescriptorSuffix], MaterialLength);
        uint[] state = WordsLittleEndian(InitialSessionKey);
        uint[]? firstFinal = null;
        for (int offset = 0; offset < MaterialLength; offset += BlockLength)
        {
            ReadOnlySpan<byte> block = padded.AsSpan(offset, BlockLength);
            uint[] add = WordsLittleEndian(SapHash(block));
            for (int i = 0; i < 4; i++)
                state[i] += add[i];
            state = Md5Compress(state, block, Mutation.Cycle);
            if (offset == MaterialLength - BlockLength)
            {
                firstFinal = state;
                state = Md5Compress(state, block, Mutation.Cycle);
            }
        }
        byte[] head = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(head, firstFinal![0]);
        return [.. head, .. BytesBigEndian(state)];
    }

    private static byte[][] Masks(byte[] seed)
    {
        uint[] state = [0x1d4a4587, 0x92f39fcc, 0x1d87d836, 0xcdc86697];
        byte[][] masks = new byte[9][];
        for (int i = 0; i < masks.Length; i++)
        {
            byte[] block = new byte[BlockLength];
            seed.AsSpan(0, 20).CopyTo(block);
            block[20] = (byte)i;
            MaskSuffix.CopyTo(block, 21);
            block[36] = 0x80;
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(56), 0x320);
            masks[i] = BytesBigEndian(Md5Compress(state, block, Mutation.Swap));
        }
        return masks;
    }

    private static byte[] Digest32(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        byte[] block = new byte[BlockLength];
        left[..16].CopyTo(block);
        right[..16].CopyTo(block.AsSpan(16));
        block[32] = 0x80;
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(56), 0x100);
        return BytesBigEndian(Md5Compress([0xb9f3dcdc, 0xfbdc740b, 0x60f77f86, 0x51907216], block, Mutation.Swap));
    }

    private static void Mix(FairPlayTables.NetworkTables tables, byte[] state, byte[] substituted)
    {
        for (int word = 0; word < 4; word++)
        {
            int offset = word * 4;
            for (int output = 0; output < 4; output++)
            {
                int mixed = 0;
                for (int input = 0; input < 4; input++)
                    mixed ^= tables.Mix(input, output, substituted[offset + input]);
                state[offset + output] = (byte)mixed;
            }
        }
    }

    private static byte[] FirstNetwork(byte[][] masks)
    {
        FairPlayTables.NetworkTables tables = FairPlayTables.First;
        byte[] state = FixedBlock.Zip(FairPlayTables.FirstInputMask, (a, b) => (byte)(a ^ b)).ToArray();
        byte[] substituted = new byte[16];
        for (int bank = 0; bank < 9; bank++)
        {
            for (int output = 0; output < 16; output++)
            {
                int input = FirstPositions[output];
                substituted[output] = (byte)tables.Round(bank, input, state[input]);
            }
            Mix(tables, state, substituted);
            for (int i = 0; i < 16; i++)
                state[i] ^= masks[bank][i];
        }
        return FirstPositions.Select(input => (byte)tables.Final(input, state[input])).ToArray();
    }

    private static byte[] SecondNetwork(byte[] left, byte[][] masks)
    {
        FairPlayTables.NetworkTables tables = FairPlayTables.Second;
        byte[] state = (byte[])left.Clone();
        byte[] substituted = new byte[16];
        for (int bank = 8; bank >= 0; bank--)
        {
            for (int output = 0; output < 16; output++)
                substituted[output] = (byte)(tables.Round(bank, output, state[SecondPositions[output]]) ^ masks[bank][output]);
            Mix(tables, state, substituted);
        }
        byte[] result = new byte[16];
        for (int output = 0; output < 16; output++)
            result[output] = (byte)(tables.Final(output, state[SecondPositions[output]]) ^ FairPlayTables.SecondOutputMask[output]);
        return result;
    }

    /// <summary>Los 20 bytes finales de m3 (y la confirmación que repite m4).</summary>
    internal static byte[] Exchange(byte[] m3Sap, byte[] m2Sap)
    {
        byte[][] masks = Masks(Descriptor(m3Sap, m2Sap));
        byte[] left = Digest32(FirstNetwork(masks), FixedBlock);
        byte[] whitebox = SecondNetwork(left, masks);
        return [.. whitebox.AsSpan(0, 4), .. Digest32(left, whitebox)];
    }

    internal static byte[] DeriveWrappingKey(byte[] receiverSap, byte[] message)
    {
        byte[] material = Md5Padded([.. KdfPrefix, .. SapCipher.DecryptMessage(message), .. receiverSap, .. KdfSuffix], MaterialLength);
        uint[] state = WordsLittleEndian(InitialSessionKey);
        for (int offset = 0; offset < MaterialLength; offset += BlockLength)
        {
            ReadOnlySpan<byte> block = material.AsSpan(offset, BlockLength);
            uint[] modified = Md5Compress(state, block, Mutation.Kdf);
            uint[] hashed = WordsLittleEndian(SapHash(block));
            state = [modified[0] + hashed[0], modified[1] + hashed[1], modified[2] + hashed[2], modified[3] + hashed[3]];
        }
        return BytesBigEndian(state);
    }
}
