// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace AireCast.Core.Pairing;

public enum PairingKind
{
    /// <summary>Apple TV antiguos: sin cifrado del canal.</summary>
    Legacy,

    /// <summary>HomeKit (Apple TV modernos, Roku, Samsung, LG…): canal cifrado.</summary>
    HomeKit,
}

/// <summary>Credenciales de emparejamiento, en el formato que guarda la app (ltpk:ltsk:atv_id:client_id).</summary>
public sealed record Credentials(byte[] Ltpk, byte[] Ltsk, byte[] AccessoryId, byte[] ClientId)
{
    public PairingKind Kind => Ltpk.Length == 0 ? PairingKind.Legacy : PairingKind.HomeKit;

    /// <summary>El texto que se guarda en credentials.json.</summary>
    public override string ToString() =>
        string.Join(':', new[] { Ltpk, Ltsk, AccessoryId, ClientId }.Select(Convert.ToHexStringLower));

    /// <exception cref="FormatException">El texto no tiene ese formato.</exception>
    public static Credentials Parse(string text)
    {
        string[] parts = text.Split(':');
        return parts.Length switch
        {
            2 => new Credentials([], Convert.FromHexString(parts[1]), [], Convert.FromHexString(parts[0])),
            4 => new Credentials(Convert.FromHexString(parts[0]), Convert.FromHexString(parts[1]),
                Convert.FromHexString(parts[2]), Convert.FromHexString(parts[3])),
            _ => throw new FormatException("Credenciales no válidas"),
        };
    }
}

/// <summary>El credentials.json de la app: identificador del receptor -> credenciales.</summary>
public static class CredentialStore
{
    /// <summary>La carpeta de la app: ~/.config/AireCast en Linux y %APPDATA%\AireCast en Windows.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AireCast", "credentials.json");

    /// <summary>Guarda las credenciales de un receptor, conservando las demás.</summary>
    public static void Save(string identifier, Credentials credentials, string? path = null)
    {
        path ??= DefaultPath;
        var entries = new Dictionary<string, string>();
        if (File.Exists(path))
        {
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
            foreach (JsonProperty entry in json.RootElement.EnumerateObject())
                entries[entry.Name] = entry.Value.GetString() ?? "";
        }
        entries[identifier] = credentials.ToString();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream file = File.Create(path);
        using var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        foreach (var (name, value) in entries)
            writer.WriteString(name, value);
        writer.WriteEndObject();
    }

    /// <summary>Las credenciales del primero de esos identificadores que las tenga, o null.</summary>
    public static Credentials? Find(IEnumerable<string> identifiers, string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return null;
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        foreach (string identifier in identifiers)
        {
            if (json.RootElement.TryGetProperty(identifier, out JsonElement value) && value.GetString() is { } text)
                return Credentials.Parse(text);
        }
        return null;
    }
}
