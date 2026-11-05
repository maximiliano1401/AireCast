// SPDX-License-Identifier: GPL-3.0-or-later
namespace AireCast.Core.Mirror;

/// <summary>El reloj de las respuestas NTP y de las marcas de tiempo de cada fotograma (formato 32.32).</summary>
public static class NtpClock
{
    /// <summary>Segundos entre 1900 (época NTP) y 1970.</summary>
    private const ulong EpochSeconds = 0x83AA7E80;

    /// <summary>Hora para las respuestas NTP: contada desde 1900.</summary>
    public static ulong Now() => FromUnixTicks(DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) + (EpochSeconds << 32);

    /// <summary>
    /// Marca de un fotograma: el mismo reloj, pero contado desde 1970, como la manda un iPhone.
    /// Con la época NTP, los receptores de Apple dejan los fotogramas ~70 años «en el futuro»:
    /// muestran el primero y la imagen se congela.
    /// </summary>
    public static ulong FrameTime() => Now() - (EpochSeconds << 32);

    private static ulong FromUnixTicks(long ticks)
    {
        ulong seconds = (ulong)(ticks / TimeSpan.TicksPerSecond);
        ulong remainder = (ulong)(ticks % TimeSpan.TicksPerSecond);
        return seconds << 32 | (remainder << 32) / TimeSpan.TicksPerSecond;
    }
}
