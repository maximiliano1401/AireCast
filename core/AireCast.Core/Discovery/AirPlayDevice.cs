// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;

namespace AireCast.Core.Discovery;

/// <summary>Receptor AirPlay encontrado en la red.</summary>
public sealed record AirPlayDevice
{
    private const uint PinRequired = 0x8;
    private const uint LegacyPairing = 0x200;

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

    private uint Flags => ParseHex(Properties.GetValueOrDefault("sf") ?? Properties.GetValueOrDefault("flags"));

    private static uint ParseHex(string? value) =>
        uint.TryParse(value?.Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out uint parsed) ? parsed : 0;
}
