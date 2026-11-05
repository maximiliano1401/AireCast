// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Audio;

namespace AireCast.Core.Mirror;

/// <summary>
/// Cuánto margen se da al audio frente a una red con tirones (Wi-Fi saturado o en 2,4 GHz). «Normal» no
/// cambia nada. «Inestable» sube el margen del audio, que la TV guarda en un búfer antes de reproducirlo, y
/// retrasa el vídeo lo mismo para que sigan sincronizados: más retraso a cambio de un sonido sin cortes.
/// </summary>
public sealed record NetworkProfile(string Name, int AudioMarginMs)
{
    /// <summary>
    /// Margen del audio en una red inestable. Los picos de latencia de un Wi-Fi malo rondan los 140 ms: con
    /// el margen normal (25 ms) esos paquetes llegan tarde y la TV deja un hueco.
    /// </summary>
    public const int UnstableAudioMarginMs = 150;

    /// <summary>El de siempre: el margen que cada TV necesita, sin añadir nada.</summary>
    public static readonly NetworkProfile Normal = new("normal", 0);

    public static readonly NetworkProfile Unstable = new("unstable", UnstableAudioMarginMs);

    public static readonly IReadOnlyList<NetworkProfile> Choices = [Normal, Unstable];

    /// <exception cref="ArgumentException">Ese perfil no existe.</exception>
    public static NetworkProfile Parse(string name) =>
        Choices.FirstOrDefault(profile => profile.Name == name)
            ?? throw new ArgumentException($"Perfil de red no admitido: {name} (valores posibles: {string.Join(", ", Choices.Select(p => p.Name))})");

    /// <summary>El margen del audio, en muestras: el que pide la TV, o el del perfil si es mayor.</summary>
    public int AudioLatency(int normalLatency) => Math.Max(normalLatency, AudioFormat.Rate * AudioMarginMs / 1000);

    /// <summary>Cuánto retrasar el vídeo para seguir sincronizado con el audio, que ahora sale más tarde.</summary>
    public TimeSpan VideoDelay(int normalLatency) =>
        TimeSpan.FromSeconds((double)(AudioLatency(normalLatency) - normalLatency) / AudioFormat.Rate);
}
