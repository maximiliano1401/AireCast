// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using AireCast.Core.Discovery;

namespace AireCast.Core.Tests.Discovery;

public class AirPlayDeviceTests
{
    private static AirPlayDevice Device(params (string Key, string Value)[] properties) => new()
    {
        Id = "id",
        Identifiers = ["id"],
        Name = "TV",
        Address = IPAddress.Loopback,
        Port = 7000,
        Properties = properties.ToDictionary(property => property.Key, property => property.Value),
    };

    [Theory]
    [InlineData("0x4", false, false)]
    [InlineData("0x8", true, false)]
    [InlineData("0x200", true, false)]
    [InlineData("0x80", false, true)]
    [InlineData("0x88", true, true)]
    public void ReadsWhatTheReceiverAsksFor(string flags, bool pairing, bool password)
    {
        AirPlayDevice device = Device(("sf", flags));

        Assert.Equal(pairing, device.NeedsPairing);
        Assert.Equal(password, device.NeedsPassword);
    }

    [Fact]
    public void ReadsThePasswordProperty()
    {
        Assert.True(Device(("pw", "true")).NeedsPassword);
        Assert.False(Device(("pw", "false")).NeedsPassword);
    }
}
