// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AireCast.Core.Mirror;

/// <summary>De dónde lee FFmpeg la imagen en cada sistema.</summary>
public static partial class ScreenCapture
{
    /// <summary>Argumentos de entrada de FFmpeg para capturar la pantalla.</summary>
    /// <exception cref="PlatformNotSupportedException">Este sistema aún no tiene captura en el núcleo.</exception>
    public static string[] InputArguments(int fps)
    {
        string rate = fps.ToString(CultureInfo.InvariantCulture);
        if (OperatingSystem.IsWindows())
            return ["-f", "gdigrab", "-framerate", rate, "-i", "desktop"];
        if (OperatingSystem.IsLinux())
        {
            if (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland")
                throw new PlatformNotSupportedException("La captura de Wayland aún no está en el núcleo: usa --source test");
            return X11Arguments(rate);
        }
        throw new PlatformNotSupportedException("Sistema sin captura de pantalla");
    }

    /// <summary>Imagen de prueba generada por FFmpeg, para probar sin capturar la pantalla.</summary>
    public static string[] TestPatternArguments(VideoOptions options)
    {
        int width = options.Width ?? 1920, height = options.Height ?? 1080;
        return ["-re", "-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"testsrc2=size={width}x{height}:rate={options.Fps}")];
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
