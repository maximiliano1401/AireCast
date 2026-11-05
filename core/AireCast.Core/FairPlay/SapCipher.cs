// SPDX-License-Identifier: LGPL-3.0-or-later
namespace AireCast.Core.FairPlay;

/// <summary>Cifrado de los mensajes FPLY: AES con claves intermedias propias de cada modo, en CBC.</summary>
internal static class SapCipher
{
    internal const int Modes = 4;
    private const int Blocks = 8;
    private const int BlockSize = 16;
    private const int MessageOffset = 16;

    private static readonly byte[][] MessageIv = [Convert.FromHexString("5752f1b7549d8f870c10485a6088cadb"), Convert.FromHexString("df7b1563f005587752a90402b9a39295"), Convert.FromHexString("68b54611fb04de676c968efb8c9db0c9"), Convert.FromHexString("27078b2123361e7adc9d0b115354690d")];
    private static readonly byte[] RoundKey0 = Convert.FromHexString("d30dfdb563def72b012ead72884dca25");
    private static readonly byte[] RoundKey10 = Convert.FromHexString("cd5ecf47e9af289b51188f68d085eb69");
    private static readonly byte[][][] MiddleKeys = Convert.FromHexString(
        "6af008a6ae5118268acccb4357d1e711c6b579e0dfce8114d41d91c868513be3bafc24065ebc9cf6131652de75afd0de81a8b516836ee6493cccc125db99e0ec781b6ceb300cfe8f3c0c54c92dc7e2a3594cc871ad4a3dd851a884b0ed048f5cafea245eae1f2de557bb511b17ddcc3d8e7a945569c09856533872ca6a3a00def3ca31810cb6201b24b5c7e4ee108bc01b9018bea49613e86ab413627c84a766c94c1dc55c8bbb04a940f640f420eba5d5664239df63981372b7a8b2360b160b462cd7af87b8005a47157e32644557cff0c0ac26f01988a8573d39e298f97f6a9407abf947788ceada25ee558333edb63f820f9ed823ce2f7722c4bab1d2e33571fca0ff354f8991109961878bfa7cfb566c98381eedcfe7954f7213f61fc87e2ab2ebe11496a9655dd1b28a9910ee309cbc01d64fa41ca1f705a0ef7fb4793408f02908920c5b348d32d3470b07705b8735404470486fa4a42b04fbfde11e3cd8eba4780f69653dfc7d2b4fbfb1ac68a74a7a6696be42e1b884e1acff1311a4cc7a007cd8a3f330d3f17ba3437d52cd1edeff958a6254b20114cfddc02a48f83a36f4e069e1907b2c8c779be7336c09f6a223b4f01d8a7927a7c965de554de00cd1515d43246c87fc2f494364a4cfbcd66e3bdd6c7f1b811f90291984b5eaee530d7ae85b3040569f13130f18fbb1e50784c47a9fe10f3d5ea5432ecdd9f585b9b0368eb7d134d90ebd1b94b255a4fbb2a4352534066fdec1ce38db3d8c3b850b317a942520622d89578e9fe5a13fc3bdf569e6befc1d2fc08baf733f1d176a")
        .Chunk(BlockSize).Chunk(9).ToArray();
    private static readonly byte[] InverseSbox = Convert.FromHexString(
        "52096ad53036a538bf40a39e81f3d7fb7ce339829b2fff87348e4344c4dee9cb547b9432a6c2233dee4c950b42fac34e082ea16628d924b2765ba2496d8bd12572f8f66486689816d4a45ccc5d65b6926c704850fdedb9da5e154657a78d9d8490d8ab008cbcd30af7e45805b8b34506d02c1e8fca3f0f02c1afbd0301138a6b3a9111414f67dcea97f2cfcef0b4e67396ac7422e7ad3585e2f937e81c75df6e47f11a711d29c5896fb7620eaa18be1bfc563e4bc6d279209adbc0fe78cd5af41fdda8338807c731b11210592780ec5f60517fa919b54a0d2de57a9f93c99cefa0e03b4dae2af5b0c8ebbb3c83539961172b047eba77d626e169146355210c7d");
    private static readonly byte[] Sbox = BuildSbox();

    private static byte[] BuildSbox()
    {
        byte[] sbox = new byte[256];
        for (int i = 0; i < 256; i++)
            sbox[InverseSbox[i]] = (byte)i;
        return sbox;
    }

    /// <summary>Descifra el cuerpo SAP (bytes 16..144) de un mensaje FPLY; el modo va en el byte 12.</summary>
    internal static byte[] DecryptMessage(byte[] message)
    {
        int mode = message[12];
        byte[] plain = new byte[Blocks * BlockSize];
        for (int block = 0; block < Blocks; block++)
        {
            int start = MessageOffset + block * BlockSize;
            byte[] state = message[start..(start + BlockSize)];
            DecryptBlock(state, mode);
            ReadOnlySpan<byte> chain = block == 0 ? MessageIv[mode] : message.AsSpan(start - BlockSize, BlockSize);
            for (int i = 0; i < BlockSize; i++)
                plain[block * BlockSize + i] = (byte)(state[i] ^ chain[i]);
        }
        return plain;
    }

    internal static byte[] Encrypt(int mode, byte[] plain)
    {
        if (mode >= Modes || plain.Length != Blocks * BlockSize)
            throw new ArgumentException("Modo o longitud de mensaje FairPlay no soportados");
        byte[] output = new byte[plain.Length];
        byte[] chain = MessageIv[mode];
        for (int block = 0; block < Blocks; block++)
        {
            byte[] state = new byte[BlockSize];
            for (int i = 0; i < BlockSize; i++)
                state[i] = (byte)(plain[block * BlockSize + i] ^ chain[i]);
            EncryptBlock(state, mode);
            state.CopyTo(output, block * BlockSize);
            chain = state;
        }
        return output;
    }

    internal static byte[] DecryptBody(int mode, ReadOnlySpan<byte> payload)
    {
        if (mode >= Modes)
            throw new ArgumentException($"Modo FairPlay {mode} no soportado");
        byte[] message = new byte[MessageOffset + Blocks * BlockSize];
        message[12] = (byte)mode;
        payload.CopyTo(message.AsSpan(MessageOffset));
        return DecryptMessage(message);
    }

    private static void XorKey(byte[] state, byte[] key)
    {
        for (int i = 0; i < BlockSize; i++)
            state[i] ^= key[i];
    }

    private static void Substitute(byte[] state, byte[] box)
    {
        for (int i = 0; i < BlockSize; i++)
            state[i] = box[state[i]];
    }

    private static void ShiftRows(byte[] state, bool inverse)
    {
        byte[] previous = (byte[])state.Clone();
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                int source = (inverse ? column - row : column + row) & 3;
                state[4 * column + row] = previous[4 * source + row];
            }
        }
    }

    private static int Multiply(int a, int b)
    {
        int product = 0;
        while (b != 0)
        {
            if ((b & 1) != 0)
                product ^= a;
            a = (a & 0x80) != 0 ? (a << 1 ^ 0x1b) & 0xFF : a << 1;
            b >>= 1;
        }
        return product;
    }

    private static void MixColumns(byte[] state, bool inverse)
    {
        ReadOnlySpan<int> m = inverse ? [14, 11, 13, 9] : [2, 3, 1, 1];
        for (int offset = 0; offset < BlockSize; offset += 4)
        {
            int a = state[offset], b = state[offset + 1], c = state[offset + 2], d = state[offset + 3];
            state[offset] = (byte)(Multiply(a, m[0]) ^ Multiply(b, m[1]) ^ Multiply(c, m[2]) ^ Multiply(d, m[3]));
            state[offset + 1] = (byte)(Multiply(a, m[3]) ^ Multiply(b, m[0]) ^ Multiply(c, m[1]) ^ Multiply(d, m[2]));
            state[offset + 2] = (byte)(Multiply(a, m[2]) ^ Multiply(b, m[3]) ^ Multiply(c, m[0]) ^ Multiply(d, m[1]));
            state[offset + 3] = (byte)(Multiply(a, m[1]) ^ Multiply(b, m[2]) ^ Multiply(c, m[3]) ^ Multiply(d, m[0]));
        }
    }

    private static void DecryptBlock(byte[] state, int mode)
    {
        XorKey(state, RoundKey10);
        for (int round = 9; round > 0; round--)
        {
            ShiftRows(state, inverse: true);
            Substitute(state, InverseSbox);
            XorKey(state, MiddleKeys[mode][round - 1]);
            MixColumns(state, inverse: true);
        }
        ShiftRows(state, inverse: true);
        Substitute(state, InverseSbox);
        XorKey(state, RoundKey0);
    }

    private static void EncryptBlock(byte[] state, int mode)
    {
        XorKey(state, RoundKey0);
        Substitute(state, Sbox);
        ShiftRows(state, inverse: false);
        for (int round = 0; round < 9; round++)
        {
            MixColumns(state, inverse: false);
            XorKey(state, MiddleKeys[mode][round]);
            Substitute(state, Sbox);
            ShiftRows(state, inverse: false);
        }
        XorKey(state, RoundKey10);
    }
}
