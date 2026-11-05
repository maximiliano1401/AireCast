// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Mirror;

namespace AireCast.Core.Tests.Mirror;

public class AccessUnitAssemblerTests
{
    private sealed class Sink : IVideoSink
    {
        public int Codecs { get; private set; }
        public List<int> FrameSizes { get; } = [];

        public Task SendCodecAsync(byte[] sps, byte[] pps, CancellationToken ct)
        {
            Codecs++;
            return Task.CompletedTask;
        }

        public Task SendFrameAsync(IReadOnlyList<byte[]> nals, CancellationToken ct)
        {
            FrameSizes.Add(nals.Count);
            return Task.CompletedTask;
        }
    }

    private static readonly byte[] Aud = [0x09, 0xF0];
    private static readonly byte[] Sps = [0x67, 1];
    private static readonly byte[] Pps = [0x68, 2];
    private static readonly byte[] Idr = [0x65, 3];
    private static readonly byte[] Slice = [0x41, 4];

    private static async Task<Sink> Feed(params byte[][] nals)
    {
        var sink = new Sink();
        var assembler = new AccessUnitAssembler(sink);
        foreach (byte[] nal in nals)
            await assembler.AddAsync(nal, default);
        return sink;
    }

    [Fact]
    public async Task GroupsNalsByAud()
    {
        Sink sink = await Feed(Aud, Sps, Pps, Idr, Slice, Aud, Slice, Aud);

        Assert.Equal([2, 1], sink.FrameSizes);
    }

    [Fact]
    public async Task SendsCodecOnceWhileItDoesNotChange()
    {
        Sink sink = await Feed(Aud, Sps, Pps, Idr, Aud, Sps, Pps, Idr, Aud);

        Assert.Equal(1, sink.Codecs);
    }

    [Fact]
    public async Task SendsCodecAgainWhenItChanges()
    {
        Sink sink = await Feed(Aud, Sps, Pps, Idr, Aud, [0x67, 9], Pps, Idr, Aud);

        Assert.Equal(2, sink.Codecs);
    }

    [Fact]
    public async Task IgnoresSlicesBeforeTheCodec()
    {
        Sink sink = await Feed(Aud, Slice, Aud, Sps, Pps, Idr, Aud);

        Assert.Equal([1], sink.FrameSizes);
    }
}
