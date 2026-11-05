// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Plist;

namespace AireCast.Core.Tests.Plist;

public class BinaryPlistWriterTests
{
    [Fact]
    public void RoundTripsASetupBody()
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = "Sala ñandú",
            ["isScreenMirroringSession"] = true,
            ["off"] = false,
            ["timingPort"] = 27450L,
            ["big"] = 4330070159449382869L,
            ["negative"] = -3L,
            ["ratio"] = 1.5,
            ["key"] = new byte[] { 1, 2, 3 },
            ["long"] = new string('x', 300),
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = 110L,
                    ["timestampInfo"] = new List<object?> { new Dictionary<string, object?> { ["name"] = "SubSu" } },
                },
            },
        };

        var read = Assert.IsType<Dictionary<string, object?>>(BinaryPlistReader.Read(BinaryPlistWriter.Write(body)));

        Assert.Equal("Sala ñandú", read["name"]);
        Assert.Equal(true, read["isScreenMirroringSession"]);
        Assert.Equal(false, read["off"]);
        Assert.Equal(27450L, read["timingPort"]);
        Assert.Equal(4330070159449382869L, read["big"]);
        Assert.Equal(-3L, read["negative"]);
        Assert.Equal(1.5, read["ratio"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, read["key"]);
        Assert.Equal(new string('x', 300), read["long"]);
        var stream = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<List<object?>>(read["streams"])));
        Assert.Equal(110L, stream["type"]);
        var info = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<List<object?>>(stream["timestampInfo"])));
        Assert.Equal("SubSu", info["name"]);
    }

    [Fact]
    public void SupportsMoreThan255Objects()
    {
        var list = Enumerable.Range(0, 400).Select(i => (object?)(long)i).ToList();

        var read = Assert.IsType<List<object?>>(BinaryPlistReader.Read(BinaryPlistWriter.Write(list)));

        Assert.Equal(399L, read[399]);
    }

    [Fact]
    public void RejectsUnknownTypes() =>
        Assert.Throws<ArgumentException>(() => BinaryPlistWriter.Write(new Dictionary<string, object?> { ["x"] = DateTime.Now }));
}
