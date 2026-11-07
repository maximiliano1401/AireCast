// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Audio;
using Android.Media;
using Android.Media.Projection;
using PcmFormat = AireCast.Core.Audio.AudioFormat;

namespace AireCast.Android;

/// <summary>
/// Audio del sistema con AudioPlaybackCapture (Android 10+). Las apps pueden no permitir esta captura:
/// entonces la TV recibe silencio de esa app. Android no deja silenciar el teléfono al capturar.
/// </summary>
public sealed class SystemAudioRecorder : IDisposable
{
    private readonly AudioRecord _record;
    private readonly PcmBuffer _pcm;
    private readonly CancellationToken _ct;
    private readonly Thread _thread;

    /// <summary>Bytes capturados y el mayor nivel de sonido desde la última consulta, para el registro.</summary>
    public long Bytes { get; private set; }

    public int State => (int)_record.State;

    public int ReadPeakAndReset()
    {
        int peak = _peak;
        _peak = 0;
        return peak;
    }

    private volatile int _peak;

    public SystemAudioRecorder(MediaProjection projection, PcmBuffer pcm, CancellationToken ct)
    {
        _pcm = pcm;
        _ct = ct;
        var capture = new AudioPlaybackCaptureConfiguration.Builder(projection)
            .AddMatchingUsage(AudioUsageKind.Media)
            .AddMatchingUsage(AudioUsageKind.Game)
            .AddMatchingUsage(AudioUsageKind.Unknown)
            .Build();
        var format = new global::Android.Media.AudioFormat.Builder()
            .SetEncoding(Encoding.Pcm16bit)!
            .SetSampleRate(PcmFormat.Rate)!
            .SetChannelMask((ChannelOut)ChannelIn.Stereo)! // la API usa el mismo tipo para entrada y salida
            .Build()!;
        _record = new AudioRecord.Builder()
            .SetAudioFormat(format)!
            .SetAudioPlaybackCaptureConfig(capture)!
            .SetBufferSizeInBytes(PcmFormat.PacketBytes * 8)
            .Build()!;
        _record.StartRecording();
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "AireCast-AudioCapture" };
        _thread.Start();
    }

    private void Loop()
    {
        byte[] buffer = new byte[PcmFormat.PacketBytes];
        while (!_ct.IsCancellationRequested)
        {
            int read = _record.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                continue;
            _pcm.Write(buffer.AsSpan(0, read));
            Bytes += read;
            for (int i = 0; i + 1 < read; i += 64)
                _peak = Math.Max(_peak, Math.Abs((int)BitConverter.ToInt16(buffer, i)));
        }
    }

    public void Dispose()
    {
        _thread.Join(TimeSpan.FromSeconds(1));
        _record.Stop();
        _record.Release();
    }
}
