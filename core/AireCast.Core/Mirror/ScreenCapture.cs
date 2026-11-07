// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AireCast.Core.Mirror;

/// <summary>De dónde sale la imagen y cómo se lanza el codificador.</summary>
public interface IScreenSource : IAsyncDisposable
{
    /// <summary>Resolución y fps definitivos; en nativa, algunas fuentes solo los conocen al abrirse.</summary>
    VideoOptions Options { get; }

    /// <summary>Cómo se captura, para el registro. Se sabe del todo tras llamar a <see cref="Encoder"/>.</summary>
    string Name { get; }

    /// <summary>Prepara la captura: en Wayland el escritorio puede pedir permiso.</summary>
    Task OpenAsync(CancellationToken ct);

    ProcessStartInfo Encoder(string ffmpeg);
}

/// <summary>Una captura que FFmpeg hace por sí mismo: gdigrab, x11grab o la imagen de prueba.</summary>
public sealed class FfmpegScreenSource(string name, VideoOptions options, string[] input) : IScreenSource
{
    public VideoOptions Options => options;

    private string? _encoder;

    public string Name => _encoder is null ? name : $"{name} y {_encoder}";

    public Task OpenAsync(CancellationToken ct) => Task.CompletedTask;

    public ProcessStartInfo Encoder(string ffmpeg)
    {
        EncoderChoice encoder = EncoderSelector.Choose(ffmpeg, options);
        _encoder = encoder.Name;
        var info = new ProcessStartInfo(encoder.Ffmpeg);
        foreach (string argument in encoder.Arguments(input, options))
            info.ArgumentList.Add(argument);
        return info;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Windows: ddagrab copia la pantalla desde la GPU y sigue el ritmo de 60 fps, que gdigrab no alcanza y
/// además hace parpadear el cursor. Captura la pantalla principal. Si este FFmpeg o este equipo no lo
/// admiten (escritorio remoto, Windows antiguo), se usa gdigrab.
/// </summary>
public sealed class WindowsScreenSource(VideoOptions options) : IScreenSource
{
    private const string DownloadFrames = "hwdownload,format=bgra";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public VideoOptions Options => options;

    public string Name { get; private set; } = "gdigrab";

    public static string[] DesktopDuplicationInput(int fps) =>
        ["-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"ddagrab=output_idx=0:framerate={fps}:draw_mouse=1")];

    public Task OpenAsync(CancellationToken ct) => Task.CompletedTask;

    public ProcessStartInfo Encoder(string ffmpeg)
    {
        EncoderChoice encoder = EncoderSelector.Choose(ffmpeg, options);
        var info = new ProcessStartInfo(encoder.Ffmpeg);
        bool duplication = CanDuplicateDesktop(encoder.Ffmpeg);
        Name = $"{(duplication ? "ddagrab" : "gdigrab")} y {encoder.Name}";
        string rate = options.Fps.ToString(CultureInfo.InvariantCulture);
        string[] arguments = duplication
            ? encoder.Arguments(DesktopDuplicationInput(options.Fps), options, DownloadFrames)
            : encoder.Arguments(["-f", "gdigrab", "-framerate", rate, "-i", "desktop"], options);
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    /// <summary>Captura un fotograma de prueba: la única forma fiable de saber si ddagrab funciona aquí.</summary>
    private bool CanDuplicateDesktop(string ffmpeg)
    {
        var probe = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        string[] arguments = ["-hide_banner", "-loglevel", "error", "-nostdin", .. DesktopDuplicationInput(options.Fps),
            "-vf", DownloadFrames, "-frames:v", "1", "-f", "null", "-"];
        foreach (string argument in arguments)
            probe.ArgumentList.Add(argument);
        try
        {
            using Process process = Process.Start(probe) ?? throw new IOException("No se pudo iniciar FFmpeg");
            process.ErrorDataReceived += (_, _) => { };
            process.OutputDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            if (process.WaitForExit(ProbeTimeout))
                return process.ExitCode == 0;
            process.Kill(entireProcessTree: true);
            return false;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>La captura de pantalla de cada sistema.</summary>
public static partial class ScreenCapture
{
    /// <summary>El permiso de captura de Wayland, junto a las credenciales.</summary>
    public static string PortalTokenFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AireCast", "screencast-token");

    /// <exception cref="PlatformNotSupportedException">Este sistema no tiene captura.</exception>
    public static IScreenSource Create(VideoOptions options)
    {
        string rate = options.Fps.ToString(CultureInfo.InvariantCulture);
        if (OperatingSystem.IsWindows())
            return new WindowsScreenSource(options);
        if (OperatingSystem.IsLinux())
        {
            return Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland"
                ? new PortalScreenSource(options, PortalTokenFile)
                : new FfmpegScreenSource("x11grab", options, X11Arguments(rate));
        }
        throw new PlatformNotSupportedException("Sistema sin captura de pantalla");
    }

    /// <summary>Imagen de prueba generada por FFmpeg, para probar sin capturar la pantalla.</summary>
    public static IScreenSource TestPattern(VideoOptions options)
    {
        int width = options.Width ?? 1920, height = options.Height ?? 1080;
        return new FfmpegScreenSource("imagen de prueba", options,
            ["-re", "-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"testsrc2=size={width}x{height}:rate={options.Fps}")]);
    }

    private static string[] X11Arguments(string rate)
    {
        string display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
        if (PrimaryMonitor() is not { } monitor)
            return ["-f", "x11grab", "-framerate", rate, "-i", display];
        return ["-f", "x11grab", "-framerate", rate, "-video_size", $"{monitor.Width}x{monitor.Height}",
            "-i", $"{display}+{monitor.X},{monitor.Y}"];
    }

    /// <summary>El monitor principal según xrandr (o el primero conectado), o null si no se sabe.</summary>
    private static (string Width, string Height, string X, string Y)? PrimaryMonitor()
    {
        string output;
        try
        {
            var info = new ProcessStartInfo("xrandr", "--query") { RedirectStandardOutput = true };
            info.Environment["LC_ALL"] = "C";
            using Process process = Process.Start(info)!;
            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        return ParseMonitor(output);
    }

    public static (string Width, string Height, string X, string Y)? ParseMonitor(string xrandr)
    {
        var monitors = MonitorLine().Matches(xrandr);
        if (monitors.Count == 0)
            return null;
        Match monitor = monitors.FirstOrDefault(m => m.Groups[1].Success) ?? monitors[0];
        return (monitor.Groups[2].Value, monitor.Groups[3].Value, monitor.Groups[4].Value, monitor.Groups[5].Value);
    }

    // "eDP-1 connected primary 1366x768+0+0 (...)"
    [GeneratedRegex(@"^\S+ connected( primary)? (\d+)x(\d+)\+(\d+)\+(\d+)", RegexOptions.Multiline)]
    private static partial Regex MonitorLine();
}
