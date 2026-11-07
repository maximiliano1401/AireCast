// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using AireCast.Core.Audio;
using AireCast.Core.Discovery;
using AireCast.Core.Mirror;
using AireCast.Core.Net;
using AireCast.Core.Pairing;

try
{
    return args switch
    {
        ["discover", .. var rest] => await Discover(rest),
        ["verify", var target] => await Verify(target),
        ["mirror", var target, .. var rest] => await Mirror(target, rest),
        ["capture", .. var rest] => await Capture(rest),
        ["pair", var target] => await Pair(target),
        [LinuxAudioCap.Argument, .. var rest] => LinuxAudioCap.Run(rest),
        _ => Usage(),
    };
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or PairingException
    or NotSupportedException or PlatformNotSupportedException or ArgumentException or FormatException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("Uso: airecast discover [segundos]");
    Console.Error.WriteLine("     airecast verify <nombre, id o dirección>");
    Console.Error.WriteLine("     airecast pair <nombre, id o dirección>");
    Console.Error.WriteLine("     airecast capture [--seconds 5] [--resolution 1080p] [--fps 30] [--source screen|test] [--encoder auto|software] [--ffmpeg ruta]");
    Console.Error.WriteLine("     airecast mirror <nombre, id o dirección> [--resolution 1080p] [--fps 30] [--source screen|test] [--encoder auto|software] [--ffmpeg ruta] [--no-audio] [--network normal|unstable]");
    return 2;
}

static async Task<int> Discover(string[] args)
{
    double seconds = args.Length > 0 ? double.Parse(args[0], CultureInfo.InvariantCulture) : 5;
    var devices = await AirPlayBrowser.DiscoverAsync(TimeSpan.FromSeconds(seconds));
    foreach (AirPlayDevice device in devices)
    {
        Console.WriteLine($"{device.Name}  [{device.Model}]");
        Console.WriteLine($"  id: {device.Id}  dirección: {device.Address}:{device.Port}");
        Console.WriteLine($"  vídeo: {device.SupportsVideo}  audio: {device.HasRaop}  emparejar: {device.NeedsPairing}");
    }
    Console.WriteLine($"{devices.Count} dispositivo(s)");
    return 0;
}

static async Task<AirPlayDevice?> Find(string target)
{
    var devices = await AirPlayBrowser.DiscoverAsync(TimeSpan.FromSeconds(4));
    AirPlayDevice? device = devices.FirstOrDefault(d => d.Identifiers.Contains(target, StringComparer.OrdinalIgnoreCase)
        || d.Address.ToString() == target
        || d.Name.Replace('\u00a0', ' ').Contains(target, StringComparison.OrdinalIgnoreCase)); // Apple usa espacios duros
    if (device is null)
        Console.Error.WriteLine($"No se encontró «{target}»");
    return device;
}

static async Task<int> Verify(string target)
{
    if (await Find(target) is not { } device)
        return 1;

    using var connection = await AirPlayConnection.ConnectAsync(device.Address, device.Port);
    var client = new AirPlayClient(connection);
    PrintInfo("GET /info", await client.GetInfoAsync());

    Credentials? credentials = CredentialStore.Find(device.Identifiers);
    if (credentials is null)
    {
        Console.Error.WriteLine($"No hay credenciales para «{device.Name}» en {CredentialStore.DefaultPath}: empareja primero");
        return 1;
    }
    Console.WriteLine($"Credenciales: {credentials.Kind}");
    await client.VerifyAsync(credentials);
    Console.WriteLine("pair-verify: correcto");
    if (credentials.Kind == PairingKind.HomeKit)
        PrintInfo("GET /info por el canal cifrado", await client.GetInfoAsync());
    return 0;
}

static string Option(string[] args, string name, string fallback)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}

static IScreenSource Source(string[] args)
{
    VideoOptions options = VideoOptions.Create(Option(args, "--resolution", "1080p"), int.Parse(Option(args, "--fps", "30"), CultureInfo.InvariantCulture))
        with { UseHardware = Option(args, "--encoder", "auto") != "software" };
    return Option(args, "--source", "screen") == "test" ? ScreenCapture.TestPattern(options) : ScreenCapture.Create(options);
}

/// <summary>Captura y codifica sin TV: sirve para comprobar la captura de cada sistema.</summary>
static async Task<int> Capture(string[] args)
{
    await using IScreenSource source = Source(args);
    await source.OpenAsync(default);
    var sink = new CountingSink();
    double seconds = double.Parse(Option(args, "--seconds", "5"), CultureInfo.InvariantCulture);
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    long frames = await VideoEncoder.RunAsync(source.Encoder(Option(args, "--ffmpeg", MirrorController.FindFfmpeg())), sink, ct: stop.Token);
    Console.WriteLine($"{frames} fotogramas en {seconds} s ({frames / seconds:F1} fps), {sink.Size.Width}x{sink.Size.Height}, nivel {sink.Level}, {sink.Bytes * 8 / seconds / 1e6:F1} Mb/s");
    Console.WriteLine(source.Name);
    Console.WriteLine(sink.Pacing());
    return 0;
}

static async Task<int> Mirror(string target, string[] args)
{
    NetworkProfile network = NetworkProfile.Parse(Option(args, "--network", NetworkProfile.Normal.Name));
    await using IScreenSource source = Source(args);
    if (await Find(target) is not { } device)
        return 1;
    Credentials? credentials = CredentialStore.Find(device.Identifiers);
    if (credentials is null && device.NeedsPairing)
    {
        Console.Error.WriteLine($"No hay credenciales para «{device.Name}»: empareja primero con «airecast pair»");
        return 1;
    }

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };
    var callbacks = new MirrorCallbacks { AskCode = AskCode, Log = Console.WriteLine };
    long frames = await MirrorController.RunAsync(device, credentials, source, Option(args, "--ffmpeg", MirrorController.FindFfmpeg()),
        callbacks, !args.Contains("--no-audio"), network, stop.Token);
    Console.WriteLine($"Terminado: {frames} fotogramas");
    return 0;
}

static async Task<int> Pair(string target)
{
    if (await Find(target) is not { } device)
        return 1;
    using AirPlayPairing pairing = await AirPlayPairing.BeginAsync(device);
    Console.Write("Escribe el PIN que muestra la TV: ");
    Credentials credentials = await pairing.FinishAsync(Console.ReadLine() ?? "");
    CredentialStore.Save(device.Id, credentials);
    Console.WriteLine($"«{device.Name}» emparejado ({credentials.Kind})");
    return 0;
}

static Task<string> AskCode(bool retry, CancellationToken ct)
{
    Console.Write(retry ? "Código incorrecto. Escribe el nuevo código de la TV: " : "Escribe el código que muestra la TV: ");
    return Task.FromResult(Console.ReadLine()?.Trim() ?? "");
}

static void PrintInfo(string title, ReceiverInfo info)
{
    Console.WriteLine($"{title}: {info.Name} [{info.Model}] versión {info.SourceVersion}");
    foreach (ReceiverDisplay display in info.Displays)
        Console.WriteLine($"  pantalla {display.Width}x{display.Height}, máximo {display.MaxWidth}x{display.MaxHeight} a {display.MaxFps} fps");
}

/// <summary>Sumidero que solo cuenta, para medir la captura sin TV.</summary>
internal sealed class CountingSink : IVideoSink
{
    public (int Width, int Height) Size { get; private set; }
    public double Level { get; private set; }
    public long Bytes { get; private set; }

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<double> _intervals = [];
    private readonly List<int> _sizes = [];

    public Task SendCodecAsync(byte[] sps, byte[] pps, CancellationToken ct)
    {
        Size = H264.SpsSize(sps);
        Level = sps[3] / 10.0;
        return Task.CompletedTask;
    }

    public Task SendFrameAsync(IReadOnlyList<byte[]> nals, CancellationToken ct)
    {
        int size = nals.Sum(nal => nal.Length);
        Bytes += size;
        _sizes.Add(size);
        _intervals.Add(_clock.Elapsed.TotalMilliseconds);
        _clock.Restart();
        return Task.CompletedTask;
    }

    /// <summary>La regularidad de los fotogramas: lo que se nota como tirones aunque los fps sean buenos.</summary>
    public string Pacing()
    {
        if (_intervals.Count < 3)
            return "sin fotogramas suficientes";
        var intervals = _intervals.Skip(1).Order().ToList();
        double Percentile(double p) => intervals[(int)((intervals.Count - 1) * p)];
        return string.Create(CultureInfo.InvariantCulture,
            $"intervalo p50 {Percentile(0.5):F1} ms, p95 {Percentile(0.95):F1} ms, p99 {Percentile(0.99):F1} ms, máx. {intervals[^1]:F1} ms; fotograma medio {_sizes.Average() / 1024:F1} KB, máx. {_sizes.Max() / 1024.0:F1} KB");
    }
}
