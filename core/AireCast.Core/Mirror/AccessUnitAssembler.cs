// SPDX-License-Identifier: GPL-3.0-or-later
namespace AireCast.Core.Mirror;

/// <summary>Quien recibe el vídeo codificado: la sesión de duplicado.</summary>
public interface IVideoSink
{
    Task SendCodecAsync(byte[] sps, byte[] pps, CancellationToken ct);

    Task SendFrameAsync(IReadOnlyList<byte[]> nals, CancellationToken ct);
}

/// <summary>
/// Agrupa los NAL de x264 en fotogramas: cada AUD marca el inicio de uno. Envía SPS y PPS antes del
/// primer fotograma y otra vez si cambian. Android no tiene AUD: allí MediaCodec entrega fotogramas
/// completos y se llama directamente a <see cref="IVideoSink"/>.
/// </summary>
public sealed class AccessUnitAssembler(IVideoSink sink)
{
    private byte[]? _sps;
    private byte[]? _pps;
    private (byte[] Sps, byte[] Pps)? _sent;
    private List<byte[]> _frame = [];

    public long Frames { get; private set; }

    public async Task AddAsync(byte[] nal, CancellationToken ct)
    {
        switch (H264.NalType(nal))
        {
            case H264.NalAud:
                if (_frame.Count > 0)
                {
                    await sink.SendFrameAsync(_frame, ct);
                    Frames++;
                }
                _frame = [];
                break;
            case H264.NalSps:
                _sps = nal;
                break;
            case H264.NalPps:
                _pps = nal;
                break;
            default:
                if (_sps is not null && _pps is not null && !SameCodec(_sps, _pps))
                {
                    await sink.SendCodecAsync(_sps, _pps, ct);
                    _sent = (_sps, _pps);
                }
                if (_sent is not null)
                    _frame.Add(nal);
                break;
        }
    }

    private bool SameCodec(byte[] sps, byte[] pps) =>
        _sent is var (sentSps, sentPps) && sps.AsSpan().SequenceEqual(sentSps) && pps.AsSpan().SequenceEqual(sentPps);
}
