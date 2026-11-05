// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Pairing;

namespace AireCast.Core.Tests.Pairing;

public class Tlv8Tests
{
    [Fact]
    public void JoinsLongValuesSplitInChunks()
    {
        byte[] value = Enumerable.Range(0, 600).Select(i => (byte)i).ToArray();

        byte[] tlv = Tlv8.Write((Tlv8.EncryptedData, value), (Tlv8.SeqNo, [3]));

        Assert.Equal(255, tlv[1]);
        var read = Tlv8.Read(tlv);
        Assert.Equal(value, read[Tlv8.EncryptedData]);
        Assert.Equal([3], read[Tlv8.SeqNo]);
    }

    [Fact]
    public void RejectsTruncatedData() => Assert.Throws<FormatException>(() => Tlv8.Read(new byte[] { 3, 10, 1 }));
}
