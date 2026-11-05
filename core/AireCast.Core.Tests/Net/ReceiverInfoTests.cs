// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Net;

namespace AireCast.Core.Tests.Net;

public class ReceiverInfoTests
{
    [Fact]
    public void FallsBackToWidthAndHeight()
    {
        var display = new Dictionary<string, object?> { ["width"] = 1280L, ["height"] = 720L };

        ReceiverInfo info = ReceiverInfo.Parse(new() { ["displays"] = new List<object?> { display } });

        Assert.Equal((1280, 720), (info.Displays[0].Width, info.Displays[0].Height));
    }

    [Fact]
    public void ToleratesMissingDisplays() => Assert.Empty(ReceiverInfo.Parse([]).Displays);
}
