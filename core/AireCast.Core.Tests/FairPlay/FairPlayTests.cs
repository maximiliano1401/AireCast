// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using AireCast.Core.FairPlay;

namespace AireCast.Core.Tests.FairPlay;

/// <summary>Vectores de prueba de FairPlay SAP, los mismos de test_fairplay.py (de los tests de doubletake).</summary>
public class FairPlayTests
{
    private static readonly byte[] ReceiverSap = Convert.FromHexString("0001cc342a5e5b1a6773c20e21b8224df862481864ef810aae2e3703c8819c23539de5f5d749bc5b7a266c496283ce7f03937ae1f616de0c15ff338ccaffb09eaabbe40f5d5f558fb97f1731f8f7da60a0ec6579c33ea98312c3b67135a6694ff82305d9ba5c615fa254d2b1834583cee42d4426c835a7a5f6c8421c0da3f1c7");
    private static readonly byte[] LocalSap = Convert.FromHexString("0001e4e3dd688293e6fa66b95ba41768e587c65f750218ff1be21543d573cefb087bd36e0c6363c3c8242f4abcfa6d660b801032015405eb4ab04dda7aeff38ffb36f4cfa48f0b5d92ae363f68b45925bbe6413ab6bdc4968f548d21e67d20f1912b6820e53f1013cde29df7350a9b9fa7c51320aea62d2949786c87642e34ba");
    private static readonly byte[] CapturedM2 = Convert.FromHexString("46504c59030102000000008202034a114c26b77d4e2eec2c8f89fdb653b5b32d3576bc176816d110a14c3f53c08dbb936183bfdfe0a4f3c12e85216003b46f738c40c54da6c436d29d1b342d63c7b314309ae79a33bb1787709ef077cbfe4190117a3423e270fd1a2eac44da1a7934f59dc681d1b70783f228c4d077c2d495f5285c3bf8df586fc2ebfe17fb5b65");

    private static byte[] UnwrapKey(byte[] receiverSap, byte[] m3, byte[] ekey)
    {
        using Aes aes = Aes.Create();
        aes.Key = SapExchange.DeriveWrappingKey(receiverSap, m3);
        byte[] plain = aes.DecryptEcb(ekey.AsSpan(56, 16), PaddingMode.None);
        return plain.Zip(ekey[16..32], (a, b) => (byte)(a ^ b)).ToArray();
    }

    private static byte[] TestMessage(int mode)
    {
        byte[] message = new byte[164];
        message[12] = (byte)mode;
        for (int i = 16; i < 144; i++)
            message[i] = (byte)(i * 5 + 7);
        return message;
    }

    private static byte[] Sparse(int index)
    {
        byte[] payload = new byte[128];
        payload[index] = 0x42;
        return payload;
    }

    private static string Hex(byte[] data) => Convert.ToHexStringLower(data);

    [Fact]
    public void Primitives()
    {
        byte[] block = Enumerable.Range(0, 64).Select(i => (byte)(i * 3 + 1)).ToArray();
        uint[] words = SapExchange.Md5Compress(SapExchange.WordsLittleEndian(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray()), block, SapExchange.Mutation.Kdf);
        byte[] bytes = new byte[16];
        for (int i = 0; i < 4; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);

        Assert.Equal("f6f728cb5a4397b675664f9291b859aa", Hex(bytes));
        Assert.Equal("75498a4e218773030e9cdf04f0c49367", Hex(SapExchange.SapHash(block)));
    }

    [Fact]
    public void SapHashCorpus()
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ulong state = 0x6a09e667f3bcc909;
        for (int n = 0; n < 64; n++)
        {
            byte[] block = new byte[64];
            for (int i = 0; i < 64; i++)
            {
                state ^= state << 13;
                state ^= state >> 7;
                state ^= state << 17;
                block[i] = (byte)state;
            }
            digest.AppendData(SapExchange.SapHash(block));
        }

        Assert.Equal("36ad2a7920076af59452d9f0c91e3b7d1aebc53f9143bd6819e39119d4535c92", Hex(digest.GetHashAndReset()));
    }

    [Theory]
    [InlineData(0, "b66a3295ffa6b56e02ed1b3d67fef74b90fe148570de65e6773669126a4905d8405644cae0b2f5ed6109c099c7aea7398dac8d623fbd69b87242b374d98f89502bb5a63e29c46a8ed0e98466966191ec1e6c8675087fde21337db1c8fab4c21db824026335f6fc37e2e5b6f53357d06994bd383d6029a0aff654fb1521bcdde4", "f7dd1ccb9e745f7951a6e325d73a1f5f")]
    [InlineData(1, "0f95c6ddc8987eda18577da2db074e7c04715af8b3914a73be1b3d6c111953017ee0a39dfcab3e0d57f2f9fbd59c5e18101788c2ab8e3cbb403bcb48b53f3e5bf74f949e79fa5ca679df4bfcb33a69b1442675d03f948fe5bd0c5ffb64b73a5ab58f46d6baae097b599624147c2487991163ecffc4d966240f9526346a10fdb0", "b44ad891396f097aa309bc132f5b8889")]
    [InlineData(2, "40f18751b44d733e0aa0416401a7d3f40375fad3ce56900602578bca14660909820e6ef3a5e943cafef5370f72c52177d9b82278b414811201a3d99202bedcca26a4d1ad08bc2669f4bae6ca54b8a120d0425edb6082f51f5aecdb547bfdb319099c9ea2729ae6a1c4480827ce9991e273843cf1c7d74ebbebc2657659bcea9f", "d38cd8efecdb20f333273c4312d9b236")]
    [InlineData(3, "70a3c30edf0e1dfa1785ce4336ed547062672a47f714a0c1f89a83d95691103dfe5cf653d4cb8299793faf33fd0d4482ef5333b41ab094a90e1baf996bcf4989783f6918397fbacddaf00a2b97556dd8099841578bc5eb1444912b47298eaf356fdd6701bb3f64e725a80eb4c6f3556195de35c93e7cc703bdd24351468e9847", "769e2fe4c5ad7fbe6fd6772d00f529f4")]
    public void MessageVectors(int mode, string decrypted, string aesKey)
    {
        byte[] message = TestMessage(mode);

        byte[] plain = SapCipher.DecryptMessage(message);

        Assert.Equal(decrypted, Hex(plain));
        Assert.Equal(message[16..144], SapCipher.Encrypt(mode, plain));
        Assert.Equal(aesKey, Hex(SapExchange.DeriveWrappingKey(ReceiverSap, message)));
    }

    [Fact]
    public void KeyUnwrapVector()
    {
        byte[] ekey = Enumerable.Range(0, 72).Select(i => (byte)(i * 7 + 3)).ToArray();

        Assert.Equal("903e5be94732428e9965afb262b193a4", Hex(UnwrapKey(ReceiverSap, TestMessage(3), ekey)));
    }

    public static TheoryData<byte[], string> ExchangeCases => new()
    {
        { new byte[128], "6f627565f3e77f5b5ede91beee7baf92e4241e0b" },
        { Enumerable.Repeat((byte)0xFF, 128).ToArray(), "dc2cc74f2ed55484f59f95b96082f0f5c017dd17" },
        { CapturedM2[14..142], "4b911e48af23d8406368aeafbb61bfcd569e3e55" },
        { Sparse(0), "9bfb9556b8659c2ac94b7ef9e587d71e159ea624" },
        { Sparse(63), "150d9fa4eb456e73ba48de5779c5c996b16b3b23" },
        { Sparse(64), "a167db30424ff8890d085c0f1c92b2c5cc06fc45" },
        { Sparse(127), "d246ec5e7adc8118994b8df77146529486ac7caf" },
    };

    [Theory]
    [MemberData(nameof(ExchangeCases))]
    public void ExchangeGoldenVectors(byte[] payload, string expected) =>
        Assert.Equal(expected, Hex(SapExchange.Exchange(LocalSap, SapCipher.DecryptBody(3, payload))));

    [Fact]
    public void Descriptor()
    {
        byte[] one = new byte[128];
        one[0] = 1;

        Assert.Equal("7e38958ffe4ed433743919fe7eb16376afa4eb9e", Hex(SapExchange.Descriptor(LocalSap, SapCipher.DecryptBody(3, new byte[128]))));
        Assert.Equal("ea46797d726c6a9be43ffa72385ff97ce1c54f1b", Hex(SapExchange.Descriptor(LocalSap, SapCipher.DecryptBody(3, one))));
    }

    [Fact]
    public void SessionExchange()
    {
        byte[] m2 = new byte[142];
        "FPLY"u8.CopyTo(m2);
        m2[4] = 3;
        m2[5] = 1;
        m2[6] = 2;
        BinaryPrimitives.WriteInt32BigEndian(m2.AsSpan(8), 130);
        m2[12] = 2;
        m2[13] = 3;
        byte[] entropy = Enumerable.Range(1, 126).Select(i => (byte)i).ToArray();
        var session = new FairPlaySession(entropy);

        Assert.Equal("46504c590301010000000004020003bb", Hex(FairPlaySession.Message1()));
        byte[] m3 = session.Message3(m2);

        Assert.Equal(164, m3.Length);
        Assert.Equal([0, 1, .. entropy], SapCipher.DecryptMessage(m3));
        byte[] receiverSap = SapCipher.DecryptBody(3, m2.AsSpan(14, 128));
        Assert.Equal(receiverSap, session.RemoteSap);
        Assert.Equal(m3[144..], SapExchange.Exchange([0, 1, .. entropy], receiverSap));

        byte[] rawKey = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        byte[] ekey = session.WrapKey(rawKey, Enumerable.Repeat((byte)0x5a, 16).ToArray());
        Assert.Equal(rawKey, UnwrapKey(receiverSap, m3, ekey));

        byte[] m4 = new byte[32];
        "FPLY"u8.CopyTo(m4);
        m4[4] = 3;
        m4[5] = 1;
        m4[6] = 4;
        BinaryPrimitives.WriteInt32BigEndian(m4.AsSpan(8), 20);
        m3.AsSpan(144).CopyTo(m4.AsSpan(12));
        session.Confirm(m4);
        m4[20] ^= 1;
        Assert.Throws<FormatException>(() => session.Confirm(m4));
    }

    [Fact]
    public void AlternateMacVector()
    {
        byte[] m2 = Convert.FromHexString("46504c5903010200000000820201cf32a25714b2524f8aa0ad7af164e37bcf4424e200047efc0ad67afcd95ded1c2730bb591b962ed63a9c4ded88ba8fc78de64d91ccfd5c7b56da88e31f5cceafc7431995a01665a54e1939d25b94db64b9e45d8d063e1e6af07e9656162b0efa404275ea5a44d9591c7256b9fbe6513898b80227721988571650942ad946688a");
        byte[] m3 = Convert.FromHexString("46504c590301030000000098018f1a9c5b9228300aafe0b41f28b66a62a6cd62bf84eb623273dead10b1f034a8d568126faa133f6ad5ab91acda3839817b4d9530b679fee43ac9e950f6e7aaf1381bd2d3d5198a03bf5648890d19234270a3583e4651893be09c6c75463c42e544fec9abc9f7722a2cc254364365ef91ded76b8c00f9674b08920fb9401e4be6d52a33f2f9ed6fadb672be45c3cde5ad94f3fea5b32ee4");
        byte[] receiverSap = SapCipher.DecryptBody(m2[13], m2.AsSpan(14, 128));
        Assert.Equal(ReceiverSap, receiverSap);

        byte[] ekey = FairPlaySession.WrapKey(receiverSap, m3, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), Convert.FromHexString("c853e777b9b65e7652d768d97c974f15"));

        Assert.Equal("46504c59010201000000003c00000000c853e777b9b65e7652d768d97c974f15000000102a53a0008888fe26bfb1e1f825f38f50d6730059", Hex(ekey[..56]));
        Assert.Equal("2fd95dc2c23122bc77c57b983a9188c4760db322", Hex(SapExchange.Descriptor(SapCipher.DecryptMessage(m3), receiverSap)));
    }

    [Fact]
    public void KeyDecryptVector()
    {
        byte[] m3 = Convert.FromHexString("46504c590301030000000098018f1a9c7d0af257b31f21f5c2d2bc814c032d457835ad0b06250574bbc7ab4a58cca6eead2c911d7f3e1e7ed4c058955dff3d5ceef014387a985bdb34995015e3dfbdacc56047cb926e093b13e9fdb5e1eee317c018bbc87fc5453c7671647da686da3d564875d03f8aea9d60092de06110bc7be0c16f391c369c75344ae47f33acfcf10e63a9b58bfce215e96001c49e4be967c5067f2a");
        byte[] ekey = Convert.FromHexString("46504c59010201000000003c0000000088e4f82c8178c18b4751ac24b27c0c2a00000010c899dc6965c1081de6a9d966e2ba3e34548cdbc651c322db18dc22f58fe154a60aecee18");

        Assert.Equal("8e1214398d46d72e7b1b8e32f80c8bf0", Hex(UnwrapKey(ReceiverSap, m3, ekey)));
    }
}
