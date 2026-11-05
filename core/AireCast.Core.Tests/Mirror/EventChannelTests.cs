// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using AireCast.Core.Mirror;

namespace AireCast.Core.Tests.Mirror;

public class EventChannelTests
{
    [Fact]
    public void AnswersAndKeepsTheRest()
    {
        byte[] buffer = Encoding.ASCII.GetBytes("POST /command RTSP/1.0\r\nCSeq: 5\r\nContent-Length: 2\r\n\r\nabPOST");

        string reply = Encoding.ASCII.GetString(EventChannel.TakeRequest(ref buffer)!);

        Assert.StartsWith("RTSP/1.0 200 OK\r\n", reply);
        Assert.Contains("CSeq: 5\r\n", reply);
        Assert.Equal("POST"u8.ToArray(), buffer);
    }

    [Fact]
    public void WaitsForTheWholeBody()
    {
        byte[] buffer = Encoding.ASCII.GetBytes("POST /command RTSP/1.0\r\nContent-Length: 5\r\n\r\nab");

        Assert.Null(EventChannel.TakeRequest(ref buffer));
    }
}
