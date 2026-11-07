// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;

namespace AireCast.Core.Discovery;

/// <summary>Receptor AirPlay encontrado en la red.</summary>
public sealed record AirPlayDevice
{
    private const uint PinRequired = 0x8;
    private const uint PasswordRequired = 0x80;
    private const uint LegacyPairing = 0x200;
    private const ulong UnifiedMediaControl = 1UL << 38;
    private const ulong CoreUtilsPairing = 1UL << 48;

    /// <summary>Identificador principal: el de DMAP si lo anuncia (Apple TV antiguos) o el deviceid.</summary>
    public required string Id { get; init; }

    /// <summary>Todos sus identificadores, para encontrar sus credenciales.</summary>
    public required IReadOnlyList<string> Identifiers { get; init; }

    public required string Name { get; init; }
    public required IPAddress Address { get; init; }
    public required int Port { get; init; }
    public required IReadOnlyDictionary<string, string> Properties { get; init; }
    public bool HasRaop { get; init; }

    public string Model => Properties.GetValueOrDefault("model") ?? "";

    /// <summary>Puede recibir vídeo: los HomePod solo admiten audio.</summary>
    public bool SupportsVideo => !Model.StartsWith("AudioAccessory", StringComparison.Ordinal);

    /// <summary>Hay que emparejarlo con PIN antes de usarlo.</summary>
    public bool NeedsPairing => (Flags & (PinRequired | LegacyPairing)) != 0;

    /// <summary>Tiene una contraseña de AirPlay, elegida por su dueño: no es un PIN que muestre en pantalla.</summary>
    public bool NeedsPassword => (Flags & PasswordRequired) != 0
        || string.Equals(Properties.GetValueOrDefault("pw"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Se empareja con HomeKit (AirPlay 2); si no, con el emparejamiento legacy.</summary>
    public bool UsesHomeKit => (Features & (UnifiedMediaControl | CoreUtilsPairing)) != 0;

    private uint Flags => ParseHex(Properties.GetValueOrDefault("sf") ?? Properties.GetValueOrDefault("flags"));

    /// <summary>«0x12345678» o «0x12345678,0xabcdef12», con la parte alta después.</summary>
    private ulong Features
    {
        get
        {
            string[] parts = (Properties.GetValueOrDefault("features") ?? Properties.GetValueOrDefault("ft") ?? "").Split(',');
            return ParseHex(parts[0]) | (parts.Length > 1 ? (ulong)ParseHex(parts[1]) << 32 : 0);
        }
    }

    private static uint ParseHex(string? value) =>
        uint.TryParse(value?.Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out uint parsed) ? parsed : 0;
}
