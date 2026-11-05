// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using AireCast.Core.Discovery;

namespace AireCast.Core.Tests.Discovery;

public class DnsMessageTests
{
    private static byte[] Name(params string[] labels) =>
        labels.SelectMany(l => new[] { (byte)Encoding.UTF8.GetByteCount(l) }.Concat(Encoding.UTF8.GetBytes(l)))
            .Append((byte)0).ToArray();

    private static byte[] Record(byte[] name, DnsType type, byte[] data) =>
        [.. name, 0, (byte)type, 0x80, 1, 0, 0, 0x11, 0x94, (byte)(data.Length >> 8), (byte)data.Length, .. data];

    private static byte[] Message(int answers, params byte[][] records) =>
        [0, 0, 0x84, 0, 0, 0, 0, (byte)answers, 0, 0, 0, 0, .. records.SelectMany(r => r)];

    [Fact]
    public void BuildsQuestions()
    {
        byte[] query = DnsMessage.BuildQuery([("_airplay._tcp.local", DnsType.Ptr)]);

        Assert.Equal(1, query[5]);
        Assert.Equal(Name("_airplay", "_tcp", "local"), query[12..^4]);
        Assert.Equal(new byte[] { 0, 12, 0, 1 }, query[^4..]);
    }

    [Fact]
    public void ParsesEachRecordType()
    {
        byte[] txt = [.. new[] { (byte)12 }, .. Encoding.ASCII.GetBytes("model=Apple1"), .. new[] { (byte)3 }, .. Encoding.ASCII.GetBytes("a=b")];
        byte[] srv = [0, 0, 0, 0, 0x1B, 0x58, .. Name("tv", "local")];
        byte[] message = Message(4,
            Record(Name("_airplay", "_tcp", "local"), DnsType.Ptr, Name("Sala", "_airplay", "_tcp", "local")),
            Record(Name("Sala", "_airplay", "_tcp", "local"), DnsType.Srv, srv),
            Record(Name("Sala", "_airplay", "_tcp", "local"), DnsType.Txt, txt),
            Record(Name("tv", "local"), DnsType.A, [192, 168, 1, 121]));

        var records = DnsMessage.Parse(message);

        Assert.Equal("Sala._airplay._tcp.local", Assert.IsType<PtrRecord>(records[0]).Target);
        var service = Assert.IsType<SrvRecord>(records[1]);
        Assert.Equal(("tv.local", 7000), (service.Target, service.Port));
        Assert.Equal("Apple1", Assert.IsType<TxtRecord>(records[2]).Values["model"]);
        Assert.Equal(IPAddress.Parse("192.168.1.121"), Assert.IsType<AddressRecord>(records[3]).Address);
    }

    [Fact]
    public void FollowsNameCompression()
    {
        // El nombre del segundo registro apunta al primero (offset 12).
        byte[] first = Record(Name("tv", "local"), DnsType.A, [10, 0, 0, 1]);
        byte[] second = [0xC0, 12, 0, 1, 0, 1, 0, 0, 0, 0, 0, 4, 10, 0, 0, 2];

        var records = DnsMessage.Parse(Message(2, first, second));

        Assert.Equal("tv.local", records[1].Name);
        Assert.Equal(IPAddress.Parse("10.0.0.2"), ((AddressRecord)records[1]).Address);
    }

    [Fact]
    public void RejectsCompressionLoops()
    {
        byte[] loop = [0xC0, 12, 0, 1, 0, 1, 0, 0, 0, 0, 0, 4, 1, 2, 3, 4];

        Assert.Throws<FormatException>(() => DnsMessage.Parse(Message(1, loop)));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(13)]
    [InlineData(20)]
    public void RejectsTruncatedMessages(int length)
    {
        byte[] message = Message(1, Record(Name("tv", "local"), DnsType.A, [1, 2, 3, 4]));

        Assert.Throws<FormatException>(() => DnsMessage.Parse(message.AsSpan(0, length)));
    }
}
