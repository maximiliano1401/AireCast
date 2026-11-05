// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Audio;

namespace AireCast.Core.Tests.Audio;

public class PcmBufferTests
{
    [Fact]
    public void SwapsBytesToBigEndian()
    {
        var buffer = new PcmBuffer();
        buffer.Write(new byte[] { 1, 2, 3, 4 });

        byte[] packet = buffer.TakePacket();

        Assert.Equal(new byte[] { 2, 1, 4, 3 }, packet[..4]);
        Assert.Equal(AudioFormat.PacketBytes, packet.Length);
    }

    [Fact]
    public void FillsWithSilenceWhenEmpty() => Assert.All(new PcmBuffer().TakePacket(), value => Assert.Equal(0, value));

    [Fact]
    public void DropsAudioThatIsTooOld()
    {
        var buffer = new PcmBuffer();
        byte[] old = Enumerable.Repeat((byte)1, AudioFormat.MaxBufferBytes).ToArray();
        byte[] recent = Enumerable.Repeat((byte)2, AudioFormat.MaxBufferBytes).ToArray();
        buffer.Write(old);
        buffer.Write(recent);

        Assert.All(buffer.TakePacket(), value => Assert.Equal(2, value));
    }

    [Fact]
    public void RetransmitKeepsOriginalPacket() =>
        Assert.Equal(new byte[] { 0x80, 0xD6, 0xAA, 0xBB, 0x80, 0x60, 0xAA, 0xBB, 9 },
            AudioStream.RetransmitPacket([0x80, 0x60, 0xAA, 0xBB, 9]));
}
