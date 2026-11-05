// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using AireCast.Core.Mirror;
using AireCast.Core.Pairing;
using static AireCast.Core.Tests.PyatvVectors;

namespace AireCast.Core.Tests.Mirror;

/// <summary>Los mismos bytes que produce mirror.py, la versión que funciona con las TVs.</summary>
public class VideoVectorTests
{
    private const ulong Time = 0x0123456789ABCDEF;

    [Fact]
    public void AvccMatches() => Assert.Equal(Bytes(Avcc), H264.BuildAvcc(Bytes(Sps), Bytes(Pps)));

    [Fact]
    public void CodecPacketMatches() =>
        Assert.Equal(Bytes(CodecPacket), VideoPackets.Codec(Bytes(Avcc), 1920, 1080, Time));

    [Fact]
    public void CodecPacketCarriesDisplaySize()
    {
        byte[] packet = VideoPackets.Codec(Bytes(Avcc), 1920, 1080, Time, (1280, 720));

        Assert.Equal(1920, BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(16)));
        Assert.Equal(1920, BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(40)));
        Assert.Equal(1280, BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(56)));
        Assert.Equal(720, BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(60)));
    }

    [Theory]
    [InlineData(1920, 1080, 1280, 720)]
    [InlineData(1280, 720, 1280, 720)]
    [InlineData(1920, 1200, 1152, 720)] // 16:10: sin deformar
    [InlineData(640, 480, 640, 480)] // lo pequeño no se amplía
    public void FitsImageToDisplay(int width, int height, int fitWidth, int fitHeight) =>
        Assert.Equal((fitWidth, fitHeight), MirrorSession.FitDisplay(width, height, 1280, 720));

    [Fact]
    public void EncryptedFramesMatch()
    {
        byte[] key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[][] nals =
        [
            [0x65, .. Enumerable.Range(0, 50).Select(i => (byte)i)],
            [0x41, .. Enumerable.Range(0, 9).Select(i => (byte)i)],
        ];

        for (int i = 0; i < 2; i++)
        {
            byte[] payload = VideoPackets.Avcc(nals);
            byte[] header = VideoPackets.VideoHeader(payload.Length + 16, Time + (ulong)i);
            byte[] packet = [.. header, .. PairCrypto.Seal(key, PairCrypto.CounterNonce((ulong)i), payload, header)];

            Assert.Equal(Bytes(i == 0 ? Frame0 : Frame1), packet);
        }
    }

    [Fact]
    public void TimingReplyMatches() =>
        Assert.Equal(Bytes(TimingReply), TimingServer.BuildReply(Bytes(TimingRequest), Time));
}
