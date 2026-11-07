// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;

namespace AireCast.Core.Mirror;

/// <summary>
/// Un codificador H.264 por hardware de FFmpeg: quita al procesador la parte más pesada del duplicado.
/// Todos dan lo mismo que x264: perfil baseline, sin fotogramas B, AUD y SPS/PPS repetidos.
/// </summary>
public sealed record HardwareCodec(string Name, string[] Device, string? Upload, string PixelFormat, string[] Encoder)
{
    /// <summary>Parte del tope de bitrate que se usa de media: así la Wi-Fi tiene margen para los picos.</summary>
    private const double AverageShare = 0.6;

    private static readonly string[] Common = ["-bf", "0", "-aud", "1", "-bsf:v", "dump_extra=freq=keyframe"];

    private static HardwareCodec Vaapi(string device) => new("VAAPI", ["-vaapi_device", device], "format=nv12,hwupload", "nv12",
        ["-c:v", "h264_vaapi", "-profile:v", "constrained_baseline", "-rc_mode", "VBR", "-sei", "0"]);

    private static readonly HardwareCodec Nvenc = new("NVENC", [], null, "yuv420p",
        ["-c:v", "h264_nvenc", "-preset", "p1", "-tune", "ull", "-rc", "vbr", "-profile:v", "baseline", "-forced-idr", "1"]);

    private static readonly HardwareCodec Amf = new("AMF", [], null, "nv12",
        ["-c:v", "h264_amf", "-usage", "lowlatency", "-quality", "speed", "-rc", "vbr_peak", "-profile:v", "constrained_baseline"]);

    private static readonly HardwareCodec QuickSync = new("Quick Sync", [], null, "nv12",
        ["-c:v", "h264_qsv", "-preset", "veryfast", "-look_ahead", "0", "-async_depth", "1", "-profile:v", "baseline"]);

    /// <summary>Los que puede haber en este sistema, por orden de preferencia.</summary>
    public static IEnumerable<HardwareCodec> Candidates()
    {
        if (OperatingSystem.IsWindows())
            return [Nvenc, Amf, QuickSync];
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev/dri"))
            return [Nvenc];
        return [.. Directory.GetFiles("/dev/dri", "renderD*").Order().Select(Vaapi), Nvenc];
    }

    /// <summary>Lo que va tras el filtro de vídeo: el codificador y su ritmo de bits.</summary>
    public string[] Arguments(VideoOptions options)
    {
        string fps = options.Fps.ToString(CultureInfo.InvariantCulture);
        string peak = options.MaxBitrate.ToString(CultureInfo.InvariantCulture) + "M";
        string average = Math.Round(options.MaxBitrate * AverageShare, 1).ToString(CultureInfo.InvariantCulture) + "M";
        return [.. Encoder, "-b:v", average, "-maxrate", peak, "-bufsize", peak, "-g", fps, .. Common];
    }
}

/// <summary>El FFmpeg y el codificador de un duplicado. Sin <paramref name="Hardware"/>, x264.</summary>
public sealed record EncoderChoice(string Ffmpeg, HardwareCodec? Hardware)
{
    public string Name => Hardware?.Name ?? "x264";

    /// <summary>El formato de la imagen que espera si se le pasa vídeo crudo.</summary>
    public string PixelFormat => Hardware?.PixelFormat ?? "yuv420p";

    public string[] Arguments(IEnumerable<string> input, VideoOptions options, string? inputFilter = null) =>
        VideoEncoder.Arguments(input, options, inputFilter, Hardware);
}

/// <summary>Elige el codificador probándolos: que FFmpeg lo incluya no significa que este equipo lo tenga.</summary>
public static class EncoderSelector
{
    private const string SystemFfmpeg = "ffmpeg";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly VideoOptions ProbeOptions = new(640, 360, 30);
    private static readonly string[] ProbeInput = ["-f", "lavfi", "-i", "color=black:size=640x360:rate=30", "-frames:v", "5"];
    private static readonly Dictionary<string, EncoderChoice> Chosen = [];

    /// <summary>
    /// El mejor que funcione aquí, y si no, x264 con <paramref name="ffmpeg"/>. También prueba el FFmpeg del
    /// sistema: el que incluyen los paquetes de Linux es estático y no accede a la GPU.
    /// </summary>
    public static EncoderChoice Choose(string ffmpeg, VideoOptions options)
    {
        if (!options.UseHardware)
            return new EncoderChoice(ffmpeg, null);
        lock (Chosen)
        {
            if (!Chosen.TryGetValue(ffmpeg, out EncoderChoice? choice))
                Chosen[ffmpeg] = choice = Find(ffmpeg);
            return choice;
        }
    }

    private static EncoderChoice Find(string ffmpeg)
    {
        foreach (string candidate in new[] { ffmpeg, SystemFfmpeg }.Distinct())
        {
            foreach (HardwareCodec codec in HardwareCodec.Candidates())
            {
                if (Works(new EncoderChoice(candidate, codec)))
                    return new EncoderChoice(candidate, codec);
            }
        }
        return new EncoderChoice(ffmpeg, null);
    }

    /// <summary>Codifica unos fotogramas y comprueba que salen con lo que necesita el troceo: AUD y SPS.</summary>
    private static bool Works(EncoderChoice choice)
    {
        var info = new ProcessStartInfo(choice.Ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in choice.Arguments(ProbeInput, ProbeOptions))
            info.ArgumentList.Add(argument);
        try
        {
            using Process process = Process.Start(info) ?? throw new IOException("No se pudo iniciar FFmpeg");
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            using var output = new MemoryStream();
            Task copy = process.StandardOutput.BaseStream.CopyToAsync(output);
            if (!process.WaitForExit(ProbeTimeout))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            copy.Wait(ProbeTimeout);
            var types = H264.SplitBuffer(output.ToArray()).Select(H264.NalType).ToHashSet();
            return process.ExitCode == 0 && types.Contains(H264.NalAud) && types.Contains(H264.NalSps);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or AggregateException)
        {
            return false;
        }
    }
}
