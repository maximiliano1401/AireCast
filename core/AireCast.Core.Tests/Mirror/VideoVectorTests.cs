// SPDX-License-Identifier: GPL-3.0-or-later
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
