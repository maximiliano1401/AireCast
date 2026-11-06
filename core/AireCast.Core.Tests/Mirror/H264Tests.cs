// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Mirror;

namespace AireCast.Core.Tests.Mirror;

public class H264Tests
{
    [Theory] // SPS reales de x264 (baseline), con bytes de prevención de emulación.
    [InlineData(1280, 720, "6742c01fda014016ec0440000003004000000f23c60ca8")]
    [InlineData(1920, 1080, "6742c028da01e0089f970110000003001000000303c8f1832a")]
    [InlineData(1920, 1200, "6742c032da01e0097b0110000003001000000303c8f1832a")]
    [InlineData(3840, 2160, "6742c033da00f0010fb0110000030001000003003c8f1832a0")]
    [InlineData(1366, 768, "6742c020da01581879bc0440000003004000000f23c60ca8")]
    [InlineData(2880, 1800, "6742c033da00b4038fe5c044000003000400000300f23c60ca80")]
    public void ReadsSizeFromSps(int width, int height, string sps) =>
        Assert.Equal((width, height), H264.SpsSize(Convert.FromHexString(sps)));

    [Fact]
    public void RejectsTruncatedSps() => Assert.Throws<FormatException>(() => H264.SpsSize([0x67, 0x42, 0xC0]));

    [Fact]
    public void SplitsStartCodesOf3And4Bytes()
    {
        byte[] stream = [0, 0, 0, 1, 0x09, 0xF0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 0, 1, 0x65, 0x88];

        var nals = new AnnexBSplitter().Feed(stream);

        Assert.Equal([[0x09, 0xF0], [0x67, 0x42]], nals); // el cero final del SPS se quita; el último NAL espera
    }

    [Fact]
    public void SplitsAcrossChunks()
    {
        byte[] stream = [0, 0, 0, 1, 0x09, 0xF0, 0, 0, 0, 1, 0x65, 0x88, 0x84, 0, 0, 0, 1, 0x09, 0xF0];
        var splitter = new AnnexBSplitter();

        var nals = new List<byte[]>();
        for (int pos = 0; pos < stream.Length; pos += 3)
            nals.AddRange(splitter.Feed(stream.AsSpan(pos, Math.Min(3, stream.Length - pos))));

        Assert.Equal([[0x09, 0xF0], [0x65, 0x88, 0x84]], nals);
    }

    [Fact]
    public void SplitsACompleteBuffer()
    {
        byte[] buffer = [0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x68, 0xCE, 0, 0, 1, 0x65, 0x88];

        Assert.Equal([[0x67, 0x42], [0x68, 0xCE], [0x65, 0x88]], H264.SplitBuffer(buffer));
    }

    [Fact]
    public void ToleratesDataWithoutStartCode() => Assert.Empty(new AnnexBSplitter().Feed([0, 0]));
}
