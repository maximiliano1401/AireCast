// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using AireCast.Core.Mirror;
using AireCast.Core.Net;
using AireCast.Core.Pairing;
using static AireCast.Core.Tests.PyatvVectors;

namespace AireCast.Core.Tests.Mirror;

public class MirrorSessionTests
{
    private static async Task<(AirPlayConnection Connection, AirPlayClient Client)> ConnectAsync(FakeReceiver receiver)
    {
        var connection = await AirPlayConnection.ConnectAsync(IPAddress.Loopback, receiver.Port);
        var client = new AirPlayClient(connection);
        await client.VerifyAsync(FakeReceiver.Credentials);
        return (connection, client);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25);
        Assert.True(condition(), "No se cumplió a tiempo");
    }

    [Fact]
    public async Task FullSessionReachesTheReceiver()
    {
        await using var receiver = new FakeReceiver();
        receiver.Start();
        var (connection, client) = await ConnectAsync(receiver);
        using (connection)
        {
            await using (MirrorSession session = await MirrorSession.StartAsync(client))
            {
                await session.SendCodecAsync(Bytes(Sps), Bytes(Pps), default);
                await session.SendFrameAsync([[0x65, 1, 2, 3]], default);
                await session.SendFrameAsync([[0x41, 4], [0x41, 5]], default);
                await WaitUntil(() => receiver.Frames.Count == 2 && receiver.EventReply is not null);
            }
        }

        Assert.True(receiver.TimingAnswered);
        Assert.Equal(true, receiver.SessionSetup!["isScreenMirroringSession"]);
        Assert.Equal("NTP", receiver.SessionSetup["timingProtocol"]);
        Assert.Equal(110L, receiver.VideoStream!["type"]);
        Assert.Equal((1920, 1080), receiver.CodecSize);
        Assert.Equal(VideoPackets.Avcc([[0x65, 1, 2, 3]]), receiver.Frames.First());
        Assert.StartsWith("RTSP/1.0 200 OK", receiver.EventReply);
        Assert.Contains("CSeq: 1", receiver.EventReply);
        Assert.Equal(2, receiver.Requests.Count(r => r.StartsWith("SETUP", StringComparison.Ordinal)));
        Assert.Contains(receiver.Requests, r => r.StartsWith("RECORD", StringComparison.Ordinal));
        Assert.Equal(2, receiver.Requests.Count(r => r.StartsWith("TEARDOWN", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task LegacyIsNotSupportedYet()
    {
        await using var receiver = new FakeReceiver();
        receiver.Start();
        using var connection = await AirPlayConnection.ConnectAsync(IPAddress.Loopback, receiver.Port);

        await Assert.ThrowsAsync<NotSupportedException>(() => MirrorSession.StartAsync(new AirPlayClient(connection)));
    }

    [Fact]
    public async Task RealFfmpegStreamReachesTheReceiver()
    {
        if (!File.Exists("/usr/bin/ffmpeg") && !File.Exists("/usr/local/bin/ffmpeg"))
            return; // sin FFmpeg (por ejemplo, en Windows sin instalarlo) no se puede probar

        await using var receiver = new FakeReceiver();
        receiver.Start();
        var (connection, client) = await ConnectAsync(receiver);
        using (connection)
        {
            await using MirrorSession session = await MirrorSession.StartAsync(client);
            VideoOptions options = VideoOptions.Create("720p", 30);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            long frames = await VideoEncoder.RunAsync("ffmpeg", VideoEncoder.Arguments(ScreenCapture.TestPatternArguments(options), options),
                session, ct: stop.Token);

            Assert.InRange(frames, 20, 90);
            await WaitUntil(() => receiver.Frames.Count >= frames);
        }
        Assert.Equal((1280, 720), receiver.CodecSize);
    }
}
