// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using AireCast.Core.Discovery;

namespace AireCast.Core.Tests.Discovery;

public class RecordSetTests
{
    private const string AirPlay = "_airplay._tcp.local";
    private const string Raop = "_raop._tcp.local";
    private const string Dmap = "_touch-able._tcp.local";

    private static Dictionary<string, string> Txt(params (string Key, string Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

    private static DnsRecord[] Service(string type, string instance, string host, int port, Dictionary<string, string> txt) =>
    [
        new PtrRecord(type, $"{instance}.{type}"),
        new SrvRecord($"{instance}.{type}", host, port),
        new TxtRecord($"{instance}.{type}", txt),
    ];

    private static List<AirPlayDevice> Devices(RecordSet records) => records.Devices(AirPlay, Raop, Dmap);

    [Fact]
    public void BuildsModernDevice()
    {
        var records = new RecordSet();
        records.Add(Service(AirPlay, "Sala", "tv.local", 7000,
            Txt(("deviceid", "4A:7E:1B:B0:92:95"), ("model", "AppleTV14,1"), ("flags", "0x20244"))));
        records.Add(Service(Raop, "4A7E1BB09295@Sala", "tv.local", 7000, Txt()));
        records.Add([new AddressRecord("tv.local", IPAddress.Parse("192.168.0.52"))]);

        AirPlayDevice device = Assert.Single(Devices(records));

        Assert.Equal("4A:7E:1B:B0:92:95", device.Id);
        Assert.Equal("Sala", device.Name);
        Assert.True(device.HasRaop);
        Assert.True(device.SupportsVideo);
        Assert.True(device.NeedsPairing); // 0x200 en flags
    }

    [Fact]
    public void PrefersDmapIdentifier()
    {
        var records = new RecordSet();
        records.Add(Service(AirPlay, "Sala", "tv.local", 7000, Txt(("deviceid", "AA:BB"), ("model", "AppleTV3,2"))));
        records.Add(Service(Dmap, "C28263C67CDB2E63", "tv.local", 3689, Txt()));
        records.Add([new AddressRecord("tv.local", IPAddress.Parse("192.168.1.122"))]);

        AirPlayDevice device = Assert.Single(Devices(records));

        Assert.Equal("C28263C67CDB2E63", device.Id);
        Assert.Equal(["C28263C67CDB2E63", "AA:BB"], device.Identifiers);
    }

    [Theory] // Las features reales de los receptores probados.
    [InlineData("0x4A7FDFD5,0x3C177FDE", true)] // Apple TV 4K
    [InlineData("0x7F8AD0,0x38BCF46", true)] // Roku
    [InlineData("0x5A7FFFF7,0x1E", false)] // Apple TV 3
    public void DetectsHomeKitFromFeatures(string features, bool homeKit)
    {
        var records = new RecordSet();
        records.Add(Service(AirPlay, "Sala", "tv.local", 7000, Txt(("deviceid", "AA"), ("features", features))));
        records.Add([new AddressRecord("tv.local", IPAddress.Parse("10.0.0.5"))]);

        Assert.Equal(homeKit, Assert.Single(Devices(records)).UsesHomeKit);
    }

    [Fact]
    public void HomePodHasNoVideo()
    {
        var records = new RecordSet();
        records.Add(Service(AirPlay, "Oficina", "pod.local", 7000, Txt(("deviceid", "AA"), ("model", "AudioAccessory1,1"))));
        records.Add([new AddressRecord("pod.local", IPAddress.Parse("10.0.0.5"))]);

        Assert.False(Assert.Single(Devices(records)).SupportsVideo);
    }

    [Fact]
    public void IgnoresIncompleteServicesAndAsksForTheMissingParts()
    {
        var records = new RecordSet();
        records.Add([new PtrRecord(AirPlay, $"Sala.{AirPlay}")]);

        Assert.Empty(Devices(records));
        var pending = records.PendingQuestions([AirPlay]);
        Assert.Contains(($"Sala.{AirPlay}", DnsType.Srv), pending);
        Assert.Contains(($"Sala.{AirPlay}", DnsType.Txt), pending);

        records.Add([new SrvRecord($"Sala.{AirPlay}", "tv.local", 7000)]);
        Assert.Contains(("tv.local", DnsType.A), records.PendingQuestions([AirPlay]));
    }
}
