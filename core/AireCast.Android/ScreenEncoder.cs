// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Mirror;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Java.Nio;

namespace AireCast.Android;

/// <summary>
/// Captura y codificación de la pantalla: MediaProjection dibuja en la superficie de entrada de
/// MediaCodec (por hardware) y cada fotograma H.264 pasa a la sesión. MediaCodec entrega SPS y PPS en
/// un búfer de configuración y fotogramas completos, así que aquí no hacen falta los AUD de x264.
/// </summary>
public sealed class ScreenEncoder : IDisposable
{
    private const string Mime = "video/avc";
    private const int DequeueTimeoutUs = 100_000;

    private readonly MediaCodec _codec;
    private readonly VirtualDisplay _display;
    private readonly IVideoSink _sink;
    private readonly CancellationToken _ct;
    private readonly Thread _thread;

    public ScreenEncoder(MediaProjection projection, int width, int height, int density, int fps, int bitrate, IVideoSink sink, CancellationToken ct)
    {
        _sink = sink;
        _ct = ct;
        MediaFormat format = MediaFormat.CreateVideoFormat(Mime, width, height);
        format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
        format.SetInteger(MediaFormat.KeyBitRate, bitrate);
        format.SetInteger(MediaFormat.KeyFrameRate, fps);
        format.SetInteger(MediaFormat.KeyIFrameInterval, 1);
        format.SetInteger(MediaFormat.KeyProfile, (int)MediaCodecProfileType.Avcprofilebaseline);
        // Android solo produce fotogramas cuando cambia la pantalla: se repite el último para no pararse.
        format.SetLong(MediaFormat.KeyRepeatPreviousFrameAfter, 1_000_000 / fps);

        _codec = MediaCodec.CreateEncoderByType(Mime);
        _codec.Configure(format, null, null, MediaCodecConfigFlags.Encode);
        var surface = _codec.CreateInputSurface();
        _codec.Start();
        _display = projection.CreateVirtualDisplay("AireCast", width, height, density, (global::Android.Views.DisplayFlags)VirtualDisplayFlags.AutoMirror, surface, null, null)!;
        _thread = new Thread(Loop) { IsBackground = true, Name = "AireCast-Video" };
        _thread.Start();
    }

    public long Frames { get; private set; }

    public Exception? Failure { get; private set; }

    private void Loop()
    {
        var info = new MediaCodec.BufferInfo();
        try
        {
            while (!_ct.IsCancellationRequested)
            {
                int index = _codec.DequeueOutputBuffer(info, DequeueTimeoutUs);
                if (index < 0)
                    continue;
                byte[] data = new byte[info.Size];
                ByteBuffer buffer = _codec.GetOutputBuffer(index)!;
                buffer.Position(info.Offset);
                buffer.Get(data, 0, info.Size);
                bool config = info.Flags.HasFlag(MediaCodecBufferFlags.CodecConfig);
                _codec.ReleaseOutputBuffer(index, false);
                Send(data, config);
            }
        }
        catch (Exception ex) when (ex is IOException or Java.Lang.IllegalStateException)
        {
            Failure = ex;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Send(byte[] data, bool config)
    {
        List<byte[]> nals = H264.SplitBuffer(data);
        if (config)
        {
            byte[]? sps = nals.Find(nal => H264.NalType(nal) == H264.NalSps);
            byte[]? pps = nals.Find(nal => H264.NalType(nal) == H264.NalPps);
            if (sps is not null && pps is not null)
                _sink.SendCodecAsync(sps, pps, _ct).GetAwaiter().GetResult();
            return;
        }
        var frame = nals.FindAll(nal => H264.NalType(nal) is not (H264.NalSps or H264.NalPps or H264.NalAud));
        if (frame.Count == 0)
            return;
        _sink.SendFrameAsync(frame, _ct).GetAwaiter().GetResult();
        Frames++;
    }

    public void Dispose()
    {
        _thread.Join(TimeSpan.FromSeconds(1));
        _display.Release();
        _codec.Stop();
        _codec.Release();
    }
}
