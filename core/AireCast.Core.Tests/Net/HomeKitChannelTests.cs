// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Net;
using AireCast.Core.Pairing;
using static AireCast.Core.Tests.PyatvVectors;

namespace AireCast.Core.Tests.Net;

public class HomeKitChannelTests
{
    private static readonly byte[] Long = Enumerable.Range(0, 1500).Select(i => (byte)(i % 251)).ToArray();
    private static readonly byte[] Short = "segundo mensaje"u8.ToArray();

    [Fact]
    public void EncryptsLikePyatv()
    {
        var channel = new HomeKitChannel(Bytes(OutputKey), Bytes(InputKey));

        Assert.Equal(Bytes(Frames1), channel.Encrypt(Long));
        Assert.Equal(Bytes(Frames2), channel.Encrypt(Short));
    }

    [Fact]
    public void DecryptsFramesSplitAtAnyPoint()
    {
        // El lado que recibe usa las mismas claves al revés.
        var channel = new HomeKitChannel(Bytes(InputKey), Bytes(OutputKey));
        byte[] stream = [.. Bytes(Frames1), .. Bytes(Frames2)];

        using var output = new MemoryStream();
        for (int pos = 0; pos < stream.Length; pos += 7)
            output.Write(channel.Decrypt(stream.AsSpan(pos, Math.Min(7, stream.Length - pos))));

        Assert.Equal([.. Long, .. Short], output.ToArray());
    }

    [Fact]
    public void RejectsTamperedData()
    {
        var channel = new HomeKitChannel(Bytes(InputKey), Bytes(OutputKey));
        byte[] frame = Bytes(Frames2);
        frame[5] ^= 1;

        Assert.Throws<PairingException>(() => channel.Decrypt(frame));
    }
}
