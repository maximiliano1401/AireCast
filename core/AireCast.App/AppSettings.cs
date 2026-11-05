// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AireCast.Core.Mirror;

namespace AireCast.App;

/// <summary>La resolución y los fps elegidos, que se recuerdan en este equipo.</summary>
internal sealed record AppSettings(string Resolution, int Fps)
{
    private static readonly AppSettings Default = new("1080p", 30);

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AireCast", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(FilePath));
            string resolution = json.RootElement.GetProperty("resolution").GetString() ?? Default.Resolution;
            int fps = json.RootElement.GetProperty("fps").GetInt32();
            bool valid = VideoOptions.Resolutions.ContainsKey(resolution) && VideoOptions.FpsChoices.Contains(fps);
            return valid ? new AppSettings(resolution, fps) : Default;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException)
        {
            return Default;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using FileStream file = File.Create(FilePath);
            using var writer = new Utf8JsonWriter(file);
            writer.WriteStartObject();
            writer.WriteString("resolution", Resolution);
            writer.WriteNumber("fps", Fps);
            writer.WriteEndObject();
        }
        catch (IOException)
        {
            // Sin poder guardar solo se pierde el recuerdo de la elección.
        }
    }
}
