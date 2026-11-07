// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;

namespace AireCast.Core.Mirror;

/// <summary>Codifica con FFmpeg (H.264 baseline Annex-B) y entrega el vídeo a un <see cref="IVideoSink"/>.</summary>
public static class VideoEncoder
{
    /// <summary>
    /// Sin esto FFmpeg analiza y retiene la entrada, y suelta los fotogramas a tirones: el formato ya se
    /// conoce y aquí importa el ritmo. Medido en Wayland a 60 fps: p95 entre fotogramas de 32 a 24 ms.
    /// </summary>
    private static readonly string[] LowDelayInput = ["-fflags", "nobuffer", "-flags", "low_delay", "-probesize", "32", "-analyzeduration", "0"];

    /// <summary>
    /// Argumentos de FFmpeg. superfast y CRF 20: ~4,7 Mb/s en 1080p30 (ultrafast y CRF 23 daban 1,7 y
    /// se veía pixelado). AUD y SPS/PPS repetidos los exigen el troceo y los receptores. El nivel
    /// H.264 lo elige x264 según tamaño, fps y tope. <paramref name="inputFilter"/> va antes de escalar.
    /// </summary>
    public static string[] Arguments(IEnumerable<string> input, VideoOptions options, string? inputFilter = null)
    {
        string fps = options.Fps.ToString(CultureInfo.InvariantCulture);
        string bitrate = options.MaxBitrate.ToString(CultureInfo.InvariantCulture) + "M";
        return
        [
            "-hide_banner", "-loglevel", "error", "-nostdin",
            .. LowDelayInput,
            .. input,
            "-vf", inputFilter is null ? options.ScaleFilter : $"{inputFilter},{options.ScaleFilter}",
            "-c:v", "libx264", "-preset", "superfast", "-tune", "zerolatency", "-crf", "20",
            "-maxrate", bitrate, "-bufsize", bitrate,
            "-profile:v", "baseline", "-pix_fmt", "yuv420p",
            "-g", fps, "-keyint_min", fps,
            "-x264-params", "aud=1:repeat-headers=1",
            "-f", "h264", "-",
        ];
    }

    /// <summary>Codifica hasta que se cancela o FFmpeg termina; devuelve los fotogramas enviados.</summary>
    /// <exception cref="IOException">FFmpeg terminó por su cuenta.</exception>
    public static async Task<long> RunAsync(ProcessStartInfo info, IVideoSink sink,
        Action<long>? onProgress = null, int progressInterval = 60, CancellationToken ct = default)
    {
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.CreateNoWindow = true;

        using Process process = Process.Start(info) ?? throw new IOException("No se pudo iniciar FFmpeg");
        Task<string> errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var splitter = new AnnexBSplitter();
        var assembler = new AccessUnitAssembler(sink);
        byte[] buffer = new byte[65536];
        long reported = 0;
        try
        {
            Stream output = process.StandardOutput.BaseStream;
            while (true)
            {
                int read = await output.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    string error = (await errors).Trim();
                    throw new IOException($"FFmpeg terminó: {(error.Length > 0 ? error : "sin salida")}");
                }
                foreach (byte[] nal in splitter.Feed(buffer.AsSpan(0, read)))
                    await assembler.AddAsync(nal, ct);
                if (assembler.Frames - reported >= progressInterval)
                {
                    reported = assembler.Frames;
                    onProgress?.Invoke(reported);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return assembler.Frames;
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
}
