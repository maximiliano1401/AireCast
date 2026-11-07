// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace AireCast.Core.Mirror;

/// <summary>
/// Captura de pantalla en Wayland: portal ScreenCast (xdg-desktop-portal) + PipeWire + GStreamer.
///
///   1. Pide al portal una sesión que capture un monitor. La primera vez el escritorio muestra su
///      diálogo de permiso; la respuesta trae un token de restauración que se guarda para no volver
///      a preguntar (es de un solo uso: el portal entrega uno nuevo en cada sesión).
///   2. OpenPipeWireRemote devuelve un descriptor de PipeWire con acceso solo a ese flujo.
///   3. gst-launch-1.0 lee el flujo y entrega vídeo crudo I420 a ritmo constante a FFmpeg.
///
/// La sesión del portal dura mientras siga abierta su conexión D-Bus.
/// </summary>
public sealed class PortalScreenSource(VideoOptions requested, string tokenFile) : IScreenSource
{
    private const string Destination = "org.freedesktop.portal.Desktop";
    private const string ObjectPath = "/org/freedesktop/portal/desktop";
    private const string ScreenCast = "org.freedesktop.portal.ScreenCast";
    private const uint SourceMonitor = 1;
    private const uint CursorEmbedded = 2; // el cursor va dibujado en la imagen
    private const uint PersistUntilRevoked = 2; // el token sirve hasta que el usuario retire el permiso
    private const uint ResponseCancelled = 1;
    private const int FSetFd = 2;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(120);

    private DBusConnection? _connection;
    private string? _session;
    private SafeFileHandle? _pipeWire;
    private uint _node;

    public VideoOptions Options { get; private set; } = requested;

    public string Name { get; private set; } = "portal de Wayland";

    public async Task OpenAsync(CancellationToken ct)
    {
        _connection = new DBusConnection(DBusAddress.Session ?? throw new IOException("No hay sesión D-Bus"));
        await _connection.ConnectAsync();

        var created = await RequestAsync("CreateSession", "a{sv}", null, null,
            new() { ["session_handle_token"] = Token() }, RequestTimeout, ct);
        _session = created["session_handle"].GetString();

        var options = new Dictionary<string, VariantValue>
        {
            ["types"] = VariantValue.UInt32(SourceMonitor),
            ["multiple"] = VariantValue.Bool(false),
        };
        if ((await PropertyAsync("AvailableCursorModes") & CursorEmbedded) != 0)
            options["cursor_mode"] = VariantValue.UInt32(CursorEmbedded);
        if (await PropertyAsync("version") >= 4)
        {
            options["persist_mode"] = VariantValue.UInt32(PersistUntilRevoked);
            if (LoadToken() is { } token)
                options["restore_token"] = token;
        }
        await RequestAsync("SelectSources", "oa{sv}", _session, null, options, RequestTimeout, ct);

        var started = await RequestAsync("Start", "osa{sv}", _session, "", [], StartTimeout, ct);
        if (started.TryGetValue("restore_token", out VariantValue restore))
            SaveToken(restore.GetString());

        if (!started.TryGetValue("streams", out VariantValue streams) || streams.Count == 0)
            throw new IOException("El portal no devolvió ninguna pantalla");
        VariantValue stream = streams.GetItem(0);
        _node = stream.GetItem(0).GetUInt32();
        if (Options.Width is null)
            UseScreenSize(stream.GetItem(1));

        _pipeWire = await _connection.CallMethodAsync(CallMessage("OpenPipeWireRemote", "oa{sv}", _session, null, []),
            static (message, _) => message.GetBodyReader().ReadHandle<SafeFileHandle>()!, null);
    }

    private MessageBuffer CallMessage(string method, string signature, string? session, string? parent, Dictionary<string, VariantValue> options)
    {
        using MessageWriter writer = _connection!.GetMessageWriter();
        writer.WriteMethodCallHeader(Destination, ObjectPath, ScreenCast, method, signature);
        if (session is not null)
            writer.WriteObjectPath(session);
        if (parent is not null)
            writer.WriteString(parent);
        writer.WriteDictionary(options);
        return writer.CreateMessage();
    }

    private MessageBuffer PropertyMessage(string name)
    {
        using MessageWriter writer = _connection!.GetMessageWriter();
        writer.WriteMethodCallHeader(Destination, ObjectPath, "org.freedesktop.DBus.Properties", "Get", "ss");
        writer.WriteString(ScreenCast);
        writer.WriteString(name);
        return writer.CreateMessage();
    }

    /// <summary>Resolución nativa: la de la pantalla que informa el portal, en números pares (yuv420p).</summary>
    private void UseScreenSize(VariantValue properties)
    {
        for (int i = 0; i < properties.Count; i++)
        {
            var entry = properties.GetDictionaryEntry(i);
            if (entry.Key.GetString() == "size")
            {
                VariantValue size = entry.Value;
                Options = Options with { Width = size.GetItem(0).GetInt32() / 2 * 2, Height = size.GetItem(1).GetInt32() / 2 * 2 };
                return;
            }
        }
        throw new IOException("El portal no informó del tamaño de la pantalla: elige otra resolución");
    }

    /// <summary>
    /// Una sola línea de sh: gst-launch escribe por una tubería directa a FFmpeg, sin pasar por esta
    /// app. Para eso el hijo debe heredar el descriptor de PipeWire: se le quita FD_CLOEXEC.
    /// </summary>
    public ProcessStartInfo Encoder(string ffmpeg)
    {
        int fd = (int)_pipeWire!.DangerousGetHandle();
        if (Fcntl(fd, FSetFd, 0) != 0)
            throw new IOException("No se pudo pasar el descriptor de PipeWire a GStreamer");

        EncoderChoice encoder = EncoderSelector.Choose(ffmpeg, Options);
        Name = $"portal de Wayland y {encoder.Name}";
        string width = Text(Options.Width!.Value), height = Text(Options.Height!.Value), fps = Text(Options.Fps);
        string format = encoder.PixelFormat == "nv12" ? "NV12" : "I420";
        string caps = $"video/x-raw,format={format},width={width},height={height},framerate={fps}/1,pixel-aspect-ratio=1/1";
        // GNOME solo envía fotogramas cuando cambia la pantalla: keepalive-time repite el último para
        // que el flujo no se detenga con la imagen quieta. Se escala antes de convertir el color,
        // que así trabaja con menos píxeles. videorate numera bien los fotogramas pero los suelta a
        // ráfagas: sync=true los entrega a su hora, y sin descartar ninguno aunque lleguen tarde.
        string[] gst =
        [
            "gst-launch-1.0", "-q", "pipewiresrc", $"fd={Text(fd)}", $"path={Text(_node)}", "do-timestamp=true",
            $"keepalive-time={Text(1000 / Options.Fps)}",
            "!", "videoscale", "n-threads=0", "!", "videoconvert", "n-threads=0", "!", "videorate",
            "!", caps, "!", "fdsink", "fd=1", "sync=true", "max-lateness=-1", "qos=false",
        ];
        string[] input = ["-f", "rawvideo", "-pix_fmt", encoder.PixelFormat, "-video_size", $"{width}x{height}", "-framerate", fps, "-i", "pipe:0"];

        var info = new ProcessStartInfo("/bin/sh");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add($"{Quote(gst)} | {Quote([encoder.Ffmpeg, .. encoder.Arguments(input, Options)])}");
        return info;
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Quote(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(argument => "'" + argument.Replace("'", "'\\''", StringComparison.Ordinal) + "'"));

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int fd, int command, int argument);

    private static string Token() => "airecast_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    private Task<uint> PropertyAsync(string name) =>
        _connection!.CallMethodAsync(PropertyMessage(name),
            static (message, _) => message.GetBodyReader().ReadVariantValue().GetUInt32(), null);

    /// <summary>
    /// Llama a un método que responde con la señal Response de un objeto Request. El handle_token fija
    /// la ruta del Request, para suscribirse a la respuesta antes de llamar.
    /// </summary>
    private async Task<Dictionary<string, VariantValue>> RequestAsync(string method, string signature, string? session, string? parent,
        Dictionary<string, VariantValue> options, TimeSpan timeout, CancellationToken ct)
    {
        DBusConnection connection = _connection!;
        string token = Token();
        string sender = connection.UniqueName![1..].Replace('.', '_');
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Interface = "org.freedesktop.portal.Request",
            Member = "Response",
            Path = $"{ObjectPath}/request/{sender}/{token}",
        };
        var response = new TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Results)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable match = await connection.AddMatchAsync(rule,
            static (message, _) =>
            {
                Reader reader = message.GetBodyReader();
                uint code = reader.ReadUInt32();
                return (code, reader.ReadDictionaryOfStringToVariantValue());
            },
            (Notification<(uint, Dictionary<string, VariantValue>)> notification) =>
            {
                if (notification.HasValue)
                    response.TrySetResult(notification.Value);
                else if (notification.IsCompletion)
                    response.TrySetException(notification.CompletionReason ?? new IOException("Se cerró la conexión con el portal"));
            });

        await connection.CallMethodAsync(CallMessage(method, signature, session, parent,
            new Dictionary<string, VariantValue>(options) { ["handle_token"] = token }));

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            var (code, results) = await response.Task.WaitAsync(limit.Token);
            if (code == ResponseCancelled)
                throw new IOException("Se canceló el permiso para compartir la pantalla");
            if (code != 0)
                throw new IOException($"El portal de captura rechazó {method} (respuesta {code})");
            return results;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("No se respondió a tiempo al permiso para compartir la pantalla");
        }
    }

    private string? LoadToken()
    {
        try
        {
            string token = File.ReadAllText(tokenFile).Trim();
            return token.Length > 0 ? token : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void SaveToken(string token)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tokenFile)!);
            File.WriteAllText(tokenFile, token);
        }
        catch (IOException)
        {
            // Sin token solo se vuelve a preguntar la próxima vez.
        }
    }

    public ValueTask DisposeAsync()
    {
        _pipeWire?.Dispose();
        _connection?.Dispose(); // cierra la sesión del portal: el escritorio quita su indicador de captura
        return ValueTask.CompletedTask;
    }
}
