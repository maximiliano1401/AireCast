// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AireCast.Core.Audio;

/// <summary>
/// Captura de audio de Linux con PulseAudio (o PipeWire con pipewire-pulse), como proceso auxiliar de
/// este mismo ejecutable. Con silencio local crea una salida virtual, la pone como predeterminada,
/// mueve allí lo que ya suena y captura su monitor: el audio va solo a la TV. Termina cuando se
/// cierra su stdin (incluido si la app muere), con una señal o si parec se detiene, y siempre deja
/// la salida predeterminada y los flujos como estaban. Si muere sin restaurar, la salida anterior
/// queda guardada y <see cref="RestoreLeftovers"/> lo arregla en el siguiente arranque.
/// </summary>
public static class LinuxAudioCap
{
    public const string Argument = "--audiocap";

    private const string Sink = "airecast_sink";

    private static string StateFile =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(), "airecast-audio.json");

    private static string Pactl(params string[] arguments)
    {
        var info = new ProcessStartInfo("pactl") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["LC_ALL"] = "C";
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        using Process process = Process.Start(info) ?? throw new IOException("No se pudo iniciar pactl");
        Task<string> errors = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException(errors.Result.Trim());
        return output;
    }

    private static bool TryPactl(params string[] arguments)
    {
        try
        {
            Pactl(arguments);
            return true;
        }
        catch (IOException)
        {
            return false; // por ejemplo, el flujo terminó mientras tanto
        }
    }

    private static IEnumerable<string[]> ShortList(string kind) =>
        Pactl("list", "short", kind).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t'));

    private static string? DefaultSink() =>
        Pactl("info").Split('\n').FirstOrDefault(line => line.StartsWith("Default Sink:", StringComparison.Ordinal))?.Split(':', 2)[1].Trim();

    private static Dictionary<string, string> Sinks() =>
        ShortList("sinks").Where(row => row.Length > 1).ToDictionary(row => row[1], row => row[0]);

    private static List<string> SinkInputs(string? sinkIndex = null) =>
        ShortList("sink-inputs").Where(row => row.Length > 1 && (sinkIndex is null || row[1] == sinkIndex)).Select(row => row[0]).ToList();

    private static List<string> OurModules() =>
        ShortList("modules").Where(row => row.Length > 2 && row[1] == "module-null-sink" && row[2].Contains($"sink_name={Sink}", StringComparison.Ordinal))
            .Select(row => row[0]).ToList();

    private static void Restore(string? previous)
    {
        var sinks = Sinks();
        if (previous is not null && previous != Sink && sinks.ContainsKey(previous))
        {
            TryPactl("set-default-sink", previous);
            if (sinks.TryGetValue(Sink, out string? index))
            {
                foreach (string input in SinkInputs(index))
                    TryPactl("move-sink-input", input, previous);
            }
        }
        foreach (string module in OurModules())
            TryPactl("unload-module", module);
        File.Delete(StateFile);
    }

    /// <summary>Deshace lo que dejó una captura que murió sin restaurar. No hace nada si no quedó nada.</summary>
    public static void RestoreLeftovers()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string? previous = null;
        try
        {
            using JsonDocument state = JsonDocument.Parse(File.ReadAllText(StateFile));
            previous = state.RootElement.GetProperty("previous").GetString();
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // No hay estado guardado.
        }
        try
        {
            if (previous is not null || OurModules().Count > 0)
                Restore(previous);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            // Sin pactl no hay nada que restaurar.
        }
    }

    private static string? MuteLocal()
    {
        RestoreLeftovers();
        string? previous = DefaultSink();
        File.WriteAllText(StateFile, $"{{\"previous\": {JsonSerializer.Serialize(previous, AudioStateContext.Default.String)}}}");
        Pactl("load-module", "module-null-sink", $"sink_name={Sink}", $"rate={AudioFormat.Rate}", $"channels={AudioFormat.Channels}",
            "sink_properties=device.description=AireCast");
        Pactl("set-default-sink", Sink);
        foreach (string input in SinkInputs())
            TryPactl("move-sink-input", input, Sink);
        return previous;
    }

    /// <summary>El proceso auxiliar: escribe «ok» o «error» en stderr y el PCM por stdout.</summary>
    public static int Run(string[] arguments)
    {
        bool mute = arguments.Contains("--mute");
        string? previous = null;
        Process parec;
        try
        {
            string source;
            if (mute)
            {
                previous = MuteLocal();
                source = $"{Sink}.monitor";
            }
            else
            {
                source = $"{DefaultSink()}.monitor";
            }
            // Sin redirigir stdout, parec escribe directamente en la tubería hacia la app.
            var info = new ProcessStartInfo("parec") { RedirectStandardInput = true };
            foreach (string argument in (string[])["-d", source, "--format=s16le", $"--rate={AudioFormat.Rate}",
                         $"--channels={AudioFormat.Channels}", "--latency-msec=10", "--client-name=AireCast"])
                info.ArgumentList.Add(argument);
            parec = Process.Start(info) ?? throw new IOException("No se pudo iniciar parec");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            if (mute)
                Restore(previous);
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        using var done = new ManualResetEventSlim();
        var watchStdin = new Thread(() =>
        {
            try
            {
                using Stream input = Console.OpenStandardInput();
                input.ReadByte();
            }
            catch (IOException)
            {
            }
            done.Set();
        }) { IsBackground = true };
        watchStdin.Start();
        parec.EnableRaisingEvents = true;
        parec.Exited += (_, _) => done.Set();
        using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
        using PosixSignalRegistration sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
        using PosixSignalRegistration sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, Stop);

        void Stop(PosixSignalContext context)
        {
            context.Cancel = true;
            done.Set();
        }

        Console.Error.WriteLine("ok");
        done.Wait();

        if (!parec.HasExited)
        {
            parec.Kill();
            parec.WaitForExit(2000);
        }
        if (mute)
            Restore(previous);
        return 0;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class AudioStateContext : System.Text.Json.Serialization.JsonSerializerContext;
