// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace AireCast.Core.Mirror;

/// <summary>Resolución y fps del duplicado. Sin ancho ni alto: resolución nativa, sin escalar.</summary>
public sealed record VideoOptions(int? Width, int? Height, int Fps)
{
    private const double BitsPerPixel = 0.2;
    private const double MinBitrate = 4;
    private const double MaxBitrateLimit = 25;

    public static readonly IReadOnlyList<int> FpsChoices = [15, 24, 30, 60];

    public static readonly IReadOnlyDictionary<string, (int? Width, int? Height)> Resolutions =
        new Dictionary<string, (int?, int?)>
        {
            ["720p"] = (1280, 720),
            ["1080p"] = (1920, 1080),
            ["1440p"] = (2560, 1440),
            ["2160p"] = (3840, 2160),
            ["native"] = (null, null),
        };

    /// <summary>
    /// Lo que decodifican los Apple TV antiguos: hasta 1080p. Una pantalla 16:10 de 1920x1200 ya pide
    /// el nivel H.264 5.0, así que la resolución nativa tampoco es segura.
    /// </summary>
    public static readonly IReadOnlyList<string> LegacyResolutions = ["720p", "1080p"];

    public bool FitsLegacyReceiver => Width is <= 1920 && Height is <= 1080;

    /// <exception cref="ArgumentException">Esa resolución o esos fps no existen.</exception>
    public static VideoOptions Create(string resolution = "1080p", int fps = 30)
    {
        if (!FpsChoices.Contains(fps))
            throw new ArgumentException($"fps no admitidos: {fps} (valores posibles: {string.Join(", ", FpsChoices)})");
        if (!Resolutions.TryGetValue(resolution, out var size))
            throw new ArgumentException($"Resolución no admitida: {resolution} (valores posibles: {string.Join(", ", Resolutions.Keys)})");
        return new VideoOptions(size.Width, size.Height, fps);
    }

    /// <summary>Tope de bitrate en Mb/s: ~0,2 bits por píxel, entre 4 y 25 para no saturar la Wi-Fi.</summary>
    public double MaxBitrate => Width is not { } width || Height is not { } height
        ? MaxBitrateLimit
        : Math.Round(Math.Min(MaxBitrateLimit, Math.Max(MinBitrate, BitsPerPixel * width * height * Fps / 1e6)), 1);

    /// <summary>Filtro de FFmpeg: escala sin deformar y rellena con bandas negras, o en nativa recorta a tamaño par.</summary>
    public string ScaleFilter => Width is { } width && Height is { } height
        ? string.Create(CultureInfo.InvariantCulture,
            $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2")
        : "crop=trunc(iw/2)*2:trunc(ih/2)*2";
}
