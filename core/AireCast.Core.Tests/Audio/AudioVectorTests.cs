// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Audio;
using AireCast.Core.Mirror;
using AireCast.Core.Net;
using AireCast.Core.Pairing;
using static AireCast.Core.Tests.PyatvVectors;

namespace AireCast.Core.Tests.Audio;

/// <summary>Los mismos bytes que produce mirror.py para el audio y el vídeo legacy.</summary>
public class AudioVectorTests
{
    private static readonly byte[] Key32 = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Key16 = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] Iv = Enumerable.Range(16, 16).Select(i => (byte)i).ToArray();

    [Fact]
    public void ModernPacketsMatch()
    {
        var packetizer = new ModernAudioPacketizer(Key32, 0x11223344);

        Assert.Equal(Bytes(ModernPacket0), packetizer.Packet(Bytes(Pcm), 65535, 123456, first: true));
        Assert.Equal(Bytes(ModernPacket1), packetizer.Packet(Bytes(Pcm), 0, 123808, first: false));
    }

    [Fact]
    public void ModernSyncMatches() =>
        Assert.Equal(Bytes(ModernSync), new ModernAudioPacketizer(Key32, 1).Sync(987654321000, 123456, 4277, first: true));

    [Fact]
    public void AlacFrameMatches() => Assert.Equal(Bytes(AlacFrame), LegacyAudioPacketizer.AlacVerbatim(Bytes(Pcm)));

    [Fact]
    public void LegacyPacketMatches() =>
        Assert.Equal(Bytes(LegacyPacket), new LegacyAudioPacketizer(Key16, Iv).Packet(Bytes(Pcm), 7, 123456, first: true));

    [Fact]
    public void LegacySyncMatches() =>
        Assert.Equal(Bytes(LegacySync), new LegacyAudioPacketizer(Key16, Iv).Sync(987654321000, 123456, 3307, first: false));

    [Fact]
    public void ClockConversionsMatch()
    {
        Assert.Equal(NtpSampleCheck, AudioClock.SamplesFromNtp(0x0123456789ABCDEF));
        Assert.Equal(NtpFromSamplesCheck, AudioClock.NtpFromSamples(987654321000));
    }

    [Fact]
    public void LegacyVideoCipherMatches()
    {
        byte[] shk = Enumerable.Range(100, 16).Select(i => (byte)i).ToArray();
        var (key, iv) = MirrorSession.DeriveLegacyVideoKeys(shk, 987654321);
        Assert.Equal(Bytes(LegacyVideoKey), key);
        Assert.Equal(Bytes(LegacyVideoIv), iv);

        using var cipher = new AesCtr(key, iv);
        byte[] output = [];
        foreach (var (start, length) in ((int, int)[])[(0, 100), (5, 7), (9, 3), (1, 40)])
            output = [.. output, .. cipher.Transform(Enumerable.Range(start, length).Select(i => (byte)i).ToArray())];

        Assert.Equal(Bytes(LegacyFrames), output);
    }

    [Fact]
    public void HeartbeatMatches() =>
        Assert.Equal(Bytes(HeartbeatPacket), VideoPackets.Header(0, VideoPackets.TypeHeartbeat, 0, VideoPackets.HeartbeatOption, 0));

    [Fact]
    public void DigestMatches() =>
        Assert.Equal(DigestHeader, AirPlayClient.BuildDigest("SETUP", "rtsp://1.2.3.4/5", "airplay", "1234", "abc"));
}
