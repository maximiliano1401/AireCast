// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Audio;
using AireCast.Core.Discovery;
using AireCast.Core.Net;
using AireCast.Core.Pairing;

namespace AireCast.Core.Mirror;

/// <summary>Lo que una interfaz necesita saber y aportar durante un duplicado.</summary>
public sealed record MirrorCallbacks
{
    /// <summary>Pide el código de la TV; el argumento indica si el anterior era incorrecto.</summary>
    public required Func<bool, CancellationToken, Task<string>> AskCode { get; init; }

    /// <summary>Mensajes para el registro.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Se llama cuando el duplicado ya está enviando vídeo.</summary>
    public Action? Started { get; init; }
}

/// <summary>Un duplicado completo: captura, conexión, sesión, audio y codificación. Lo usan la consola y la app.</summary>
public static class MirrorController
{
    /// <summary>Duplica hasta que se cancela; devuelve los fotogramas enviados.</summary>
    /// <exception cref="IOException">Falló la red, la TV o el codificador.</exception>
    /// <exception cref="PairingException">La TV rechazó las credenciales.</exception>
    public static async Task<long> RunAsync(AirPlayDevice device, Credentials credentials, IScreenSource source, string ffmpeg,
        MirrorCallbacks callbacks, bool withAudio = true, CancellationToken ct = default)
    {
        // Primero la captura: en Wayland el escritorio puede pedir permiso, y la TV no debe esperar.
        await source.OpenAsync(ct);

        using AirPlayConnection connection = await AirPlayConnection.ConnectAsync(device.Address, device.Port, ct);
        var client = new AirPlayClient(connection) { AskCode = callbacks.AskCode };
        await client.VerifyAsync(credentials, ct);

        LinuxAudioCap.RestoreLeftovers();
        PcmBuffer? pcm = withAudio ? new PcmBuffer() : null;
        await using MirrorSession session = await MirrorSession.StartAsync(client, pcm, ct: ct);
        await using AudioCapture? capture = await StartAudioAsync(session, pcm, callbacks, ct);

        callbacks.Log?.Invoke($"Duplicando en «{device.Name}» a {source.Options.Fps} fps");
        callbacks.Started?.Invoke();
        long frames = await VideoEncoder.RunAsync(source.Encoder(ffmpeg), session,
            sent => callbacks.Log?.Invoke($"{sent} fotogramas enviados"), source.Options.Fps * 2, ct);
        if (session.ResentAudioPackets > 0)
            callbacks.Log?.Invoke($"Paquetes de audio reenviados: {session.ResentAudioPackets}");
        return frames;
    }

    private static async Task<AudioCapture?> StartAudioAsync(MirrorSession session, PcmBuffer? pcm, MirrorCallbacks callbacks, CancellationToken ct)
    {
        if (!session.HasAudio || pcm is null)
        {
            callbacks.Log?.Invoke("Audio: no disponible, solo vídeo");
            return null;
        }
        try
        {
            AudioCapture? capture = await AudioCapture.StartAsync(pcm, muteLocal: true, ct);
            callbacks.Log?.Invoke(capture is null ? "Audio: sin captura en este sistema, va silencio" : "Audio: activo");
            return capture;
        }
        catch (IOException ex)
        {
            callbacks.Log?.Invoke($"Audio: {ex.Message}. Sigue solo con vídeo");
            return null;
        }
    }

    /// <summary>
    /// FFmpeg junto a la app si va empaquetado; en Windows, el que instala WinGet; si no, el del PATH.
    /// </summary>
    public static string FindFfmpeg()
    {
        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string bundled = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(bundled))
            return bundled;
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("LOCALAPPDATA") is { } local)
        {
            string packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
            {
                foreach (string package in Directory.GetDirectories(packages, "Gyan.FFmpeg*"))
                {
                    foreach (string build in Directory.GetDirectories(package))
                    {
                        string exe = Path.Combine(build, "bin", name);
                        if (File.Exists(exe))
                            return exe;
                    }
                }
            }
        }
        return name;
    }
}
