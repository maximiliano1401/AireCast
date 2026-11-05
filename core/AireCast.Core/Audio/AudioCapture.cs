// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;

namespace AireCast.Core.Audio;

/// <summary>
/// Captura del audio del sistema en un proceso aparte, con un contrato común: PCM por stdout, «ok» o
/// «error: …» en stderr al iniciar, y fin cuando se cierra su stdin. Al ser otro proceso, restaura
/// el audio del PC aunque esta app muera de golpe.
/// </summary>
public sealed class AudioCapture : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);

    private readonly Process _process;
    private readonly Task _reader;

    private AudioCapture(Process process, PcmBuffer pcm)
    {
        _process = process;
        _reader = Task.Run(() => ReadAsync(process, pcm));
    }

    /// <summary>
    /// Inicia la captura: en Linux este mismo ejecutable con --audiocap, en Windows audiocap.exe.
    /// Devuelve null si este sistema no tiene captura de audio; el duplicado sigue con silencio.
    /// </summary>
    /// <exception cref="IOException">La captura no pudo iniciarse (en Windows, también si falta audiocap.exe).</exception>
    public static async Task<AudioCapture?> StartAsync(PcmBuffer pcm, bool muteLocal, CancellationToken ct = default)
    {
        if (Command(muteLocal) is not { } info)
            return null;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.CreateNoWindow = true;
        Process process = Process.Start(info) ?? throw new IOException("No se pudo iniciar la captura de audio");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(StartTimeout);
            string status = (await process.StandardError.ReadLineAsync(timeout.Token) ?? "").Trim();
            if (status != "ok")
                throw new IOException($"Captura de audio: {(status.Length > 0 ? status : "terminó al iniciar")}");
        }
        catch
        {
            process.Kill();
            process.Dispose();
            throw;
        }
        return new AudioCapture(process, pcm);
    }

    private static ProcessStartInfo? Command(bool muteLocal)
    {
        string pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            // Sin él no hay captura: lo decimos en voz alta en vez de mandar silencio a la TV sin avisar.
            string exe = Path.Combine(AppContext.BaseDirectory, "audiocap.exe");
            if (!File.Exists(exe))
                throw new IOException($"No se encontró audiocap.exe junto a la aplicación ({AppContext.BaseDirectory}); compílalo con «npm run build:audiocap»");
            info = new ProcessStartInfo(exe);
        }
        else if (OperatingSystem.IsLinux() && Environment.ProcessPath is { } self)
        {
            info = new ProcessStartInfo(self);
            info.ArgumentList.Add(LinuxAudioCap.Argument);
        }
        else
        {
            return null;
        }
        info.ArgumentList.Add(pid);
        if (muteLocal)
            info.ArgumentList.Add("--mute");
        return info;
    }

    private static async Task ReadAsync(Process process, PcmBuffer pcm)
    {
        byte[] buffer = new byte[16384];
        try
        {
            Stream output = process.StandardOutput.BaseStream;
            int read;
            while ((read = await output.ReadAsync(buffer)) > 0)
                pcm.Write(buffer.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // La captura terminó.
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close(); // restaura el audio del PC y termina sola
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            _process.Kill();
        }
        await _reader;
        _process.Dispose();
    }
}
