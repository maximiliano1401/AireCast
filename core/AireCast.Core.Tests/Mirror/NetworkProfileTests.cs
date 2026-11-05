// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Audio;
using AireCast.Core.Mirror;

namespace AireCast.Core.Tests.Mirror;

public class NetworkProfileTests
{
    private static int Samples(int milliseconds) => AudioFormat.Rate * milliseconds / 1000;

    [Theory]
    [InlineData(25)]
    [InlineData(70)]
    [InlineData(100)]
    [InlineData(400)]
    public void NormalNeverChangesAnything(int normalMs)
    {
        int normal = Samples(normalMs);

        Assert.Equal(normal, NetworkProfile.Normal.AudioLatency(normal));
        Assert.Equal(TimeSpan.Zero, NetworkProfile.Normal.VideoDelay(normal));
    }

    [Theory]
    [InlineData(25, 150, 125)]   // el margen por defecto (Roku y similares)
    [InlineData(97, 150, 53)]    // una TV que declara su latencia (27 ms + 70 ms de margen)
    [InlineData(100, 150, 50)]   // el máximo normal
    public void UnstableRaisesAudioAndDelaysVideoByTheSameAmount(int normalMs, int expectedMs, int delayMs)
    {
        int normal = Samples(normalMs);
        int latency = NetworkProfile.Unstable.AudioLatency(normal);

        Assert.Equal(Samples(expectedMs), latency);
        // El vídeo se retrasa justo lo que se retrasó el audio: siguen sincronizados.
        Assert.Equal(delayMs, NetworkProfile.Unstable.VideoDelay(normal).TotalMilliseconds, precision: 0);
        Assert.Equal(TimeSpan.FromSeconds((double)(latency - normal) / AudioFormat.Rate), NetworkProfile.Unstable.VideoDelay(normal));
    }

    [Fact]
    public void UnstableNeverLowersAMarginTheTvAlreadyAsksFor()
    {
        int generous = Samples(400);

        Assert.Equal(generous, NetworkProfile.Unstable.AudioLatency(generous));
        Assert.Equal(TimeSpan.Zero, NetworkProfile.Unstable.VideoDelay(generous));
    }

    [Fact]
    public void ParsesByNameAndRejectsTheRest()
    {
        Assert.Same(NetworkProfile.Normal, NetworkProfile.Parse("normal"));
        Assert.Same(NetworkProfile.Unstable, NetworkProfile.Parse("unstable"));
        Assert.Throws<ArgumentException>(() => NetworkProfile.Parse("rapida"));
    }

    [Fact]
    public void FrameTimeCanBeDelayedButNeverBroughtForward()
    {
        static double Milliseconds(ulong time) => (double)time / (1UL << 32) * 1000;

        ulong now = NtpClock.FrameTime();
        ulong delayed = NtpClock.FrameTime(TimeSpan.FromMilliseconds(125));
        ulong negative = NtpClock.FrameTime(TimeSpan.FromMilliseconds(-500));

        Assert.InRange(Milliseconds(delayed - now), 125, 160);
        Assert.InRange(Milliseconds(negative - now), 0, 35); // un retraso negativo se trata como cero
    }
}
