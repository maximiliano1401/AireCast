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

    [Theory]
    [InlineData("720p", true)]
    [InlineData("1080p", true)]
    [InlineData("1440p", false)]
    [InlineData("2160p", false)]
    [InlineData("native", false)]
    public void KnowsWhatLegacyReceiversDecode(string resolution, bool fits)
    {
        Assert.Equal(fits, VideoOptions.Create(resolution).FitsLegacyReceiver);
        Assert.Equal(fits, VideoOptions.LegacyResolutions.Contains(resolution));
    }

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
    public void AsksForLowDelayBeforeTheInput()
    {
        string[] arguments = VideoEncoder.Arguments(["-i", "x"], VideoOptions.Create());

        Assert.True(Array.IndexOf(arguments, "-fflags") < Array.IndexOf(arguments, "-i"));
        Assert.Equal("nobuffer", arguments[Array.IndexOf(arguments, "-fflags") + 1]);
    }

    [Fact]
    public void DownloadsDesktopDuplicationFramesBeforeScaling()
    {
        VideoOptions options = VideoOptions.Create("720p", 60);
        string[] arguments = VideoEncoder.Arguments(WindowsScreenSource.DesktopDuplicationInput(60), options, "hwdownload,format=bgra");

        Assert.Equal("ddagrab=output_idx=0:framerate=60:draw_mouse=1", arguments[Array.IndexOf(arguments, "-i") + 1]);
        Assert.Equal($"hwdownload,format=bgra,{options.ScaleFilter}", arguments[Array.IndexOf(arguments, "-vf") + 1]);
    }

    [Fact]
    public void HardwareEncodersKeepTheStreamTheReceiverNeeds()
    {
        foreach (HardwareCodec codec in HardwareCodec.Candidates())
        {
            string[] arguments = VideoEncoder.Arguments(["-i", "x"], VideoOptions.Create(), null, codec);

            Assert.Equal("0", arguments[Array.IndexOf(arguments, "-bf") + 1]);
            Assert.Equal("1", arguments[Array.IndexOf(arguments, "-aud") + 1]);
            Assert.Equal("dump_extra=freq=keyframe", arguments[Array.IndexOf(arguments, "-bsf:v") + 1]);
            Assert.Equal("12.4M", arguments[Array.IndexOf(arguments, "-maxrate") + 1]);
            Assert.Equal("7.4M", arguments[Array.IndexOf(arguments, "-b:v") + 1]);
            Assert.DoesNotContain("libx264", arguments);
        }
    }

    [Fact]
    public void WithoutHardwareTheEncoderIsX264()
    {
        EncoderChoice choice = EncoderSelector.Choose("ffmpeg", VideoOptions.Create() with { UseHardware = false });

        Assert.Equal("x264", choice.Name);
        Assert.Contains("libx264", choice.Arguments(["-i", "x"], VideoOptions.Create()));
    }

    [Fact]
    public void AMissingFfmpegDoesNotBreakTheChoice()
    {
        const string Missing = "/ruta/que/no/existe/ffmpeg";

        EncoderChoice choice = EncoderSelector.Choose(Missing, new VideoOptions(1280, 720, 30));

        Assert.True(choice.Hardware is null ? choice.Ffmpeg == Missing : choice.Ffmpeg != Missing);
    }

    [Fact]
    public void TheSystemFfmpegIsNotTheOneNextToTheApp()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string app = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
        string system = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        File.WriteAllText(Path.Combine(app, name), "");
        File.WriteAllText(Path.Combine(system, name), "");

        string? found = EncoderSelector.FindOnPath(string.Join(Path.PathSeparator, app, system), app + Path.DirectorySeparatorChar);

        Assert.Equal(Path.Combine(system, name), found);
        Assert.Null(EncoderSelector.FindOnPath(app, app));
        Directory.Delete(root, true);
    }

    [Fact]
    public void ReadsPrimaryMonitor()
    {
        const string Xrandr = "HDMI-1 connected 1920x1080+1366+0 (normal)\neDP-1 connected primary 1366x768+0+0 (normal)\n";

        Assert.Equal(("1366", "768", "0", "0"), ScreenCapture.ParseMonitor(Xrandr));
        Assert.Null(ScreenCapture.ParseMonitor("HDMI-1 disconnected"));
    }
}
