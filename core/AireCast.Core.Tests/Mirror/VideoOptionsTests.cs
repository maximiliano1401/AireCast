// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Mirror;

namespace AireCast.Core.Tests.Mirror;

public class VideoOptionsTests
{
    [Theory]
    [InlineData("720p", 30, 5.5)]
    [InlineData("1080p", 30, 12.4)]
    [InlineData("1080p", 60, 24.9)]
    [InlineData("720p", 15, 4)]
    [InlineData("2160p", 60, 25)]
    [InlineData("native", 30, 25)]
    public void BitrateScalesWithLimits(string resolution, int fps, double expected) =>
        Assert.Equal(expected, VideoOptions.Create(resolution, fps).MaxBitrate);

    [Fact]
    public void RejectsUnknownValues()
    {
        Assert.Throws<ArgumentException>(() => VideoOptions.Create("8k"));
        Assert.Throws<ArgumentException>(() => VideoOptions.Create("1080p", 25));
    }

    [Fact]
    public void KeepsTheRequiredEncoderSettings()
    {
        string[] arguments = VideoEncoder.Arguments(["-i", "x"], VideoOptions.Create());

        Assert.Equal("baseline", arguments[Array.IndexOf(arguments, "-profile:v") + 1]);
        Assert.Equal("aud=1:repeat-headers=1", arguments[Array.IndexOf(arguments, "-x264-params") + 1]);
        Assert.Equal("h264", arguments[Array.IndexOf(arguments, "-f") + 1]);
        Assert.Equal("12.4M", arguments[Array.IndexOf(arguments, "-maxrate") + 1]);
    }

    [Fact]
    public void ReadsPrimaryMonitor()
    {
        const string Xrandr = "HDMI-1 connected 1920x1080+1366+0 (normal)\neDP-1 connected primary 1366x768+0+0 (normal)\n";

        Assert.Equal(("1366", "768", "0", "0"), ScreenCapture.ParseMonitor(Xrandr));
        Assert.Null(ScreenCapture.ParseMonitor("HDMI-1 disconnected"));
    }
}
