// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Plist;
using static AireCast.Core.Tests.PyatvVectors;

namespace AireCast.Core.Tests.Plist;

public class BinaryPlistReaderTests
{
    [Fact]
    public void ReadsEveryValueType()
    {
        var plist = Assert.IsType<Dictionary<string, object?>>(BinaryPlistReader.Read(Bytes(PyatvVectors.Plist)));

        Assert.Equal("Sala ñandú", plist["name"]);
        Assert.Equal(4330070159449382869L, plist["features"]);
        Assert.Equal(7L, plist["pequeño"]);
        Assert.Equal(-3L, plist["negativo"]);
        Assert.Equal(true, plist["activo"]);
        Assert.Equal(false, plist["apagado"]);
        Assert.Equal(3.5, plist["pi"]);
        Assert.Equal(Enumerable.Range(0, 32).Select(i => (byte)i), Assert.IsType<byte[]>(plist["pk"]));
        Assert.Equal(new string('x', 300), plist["largo"]);
        var display = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<List<object?>>(plist["displays"])));
        Assert.Equal(3840L, display["widthPixelsMax"]);
    }

    [Fact]
    public void RejectsOtherData() => Assert.Throws<FormatException>(() => BinaryPlistReader.Read("no es un plist"u8.ToArray()));

    [Fact]
    public void RejectsTruncatedPlist()
    {
        byte[] data = Bytes(PyatvVectors.Plist);

        Assert.Throws<FormatException>(() => BinaryPlistReader.Read(data[..^40]));
    }
}
