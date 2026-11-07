// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AireCast.Core.Mirror;

namespace AireCast.Ui;

/// <summary>La resolución, los fps y el tipo de red elegidos, que se recuerdan en este equipo.</summary>
internal sealed record AppSettings(string Resolution, int Fps, string Network)
{
    private static readonly AppSettings Default = new("1080p", 30, NetworkProfile.Normal.Name);

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AireCast", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(FilePath));
            string resolution = json.RootElement.GetProperty("resolution").GetString() ?? Default.Resolution;
            int fps = json.RootElement.GetProperty("fps").GetInt32();
            // Los ajustes guardados por versiones anteriores no tienen «network»: se conservan los demás.
            string network = json.RootElement.TryGetProperty("network", out JsonElement saved) && saved.GetString() is { } name
                && NetworkProfile.Choices.Any(profile => profile.Name == name) ? name : Default.Network;
            bool valid = VideoOptions.Resolutions.ContainsKey(resolution) && VideoOptions.FpsChoices.Contains(fps);
            return valid ? new AppSettings(resolution, fps, network) : Default;
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
            writer.WriteString("network", Network);
            writer.WriteEndObject();
        }
        catch (IOException)
        {
            // Sin poder guardar solo se pierde el recuerdo de la elección.
        }
    }
}
