// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using AireCast.Core.Pairing;
using AireCast.Core.Plist;

namespace AireCast.Core.Net;

/// <summary>Pantalla que anuncia el receptor en /info.</summary>
public sealed record ReceiverDisplay(int Width, int Height, int MaxWidth, int MaxHeight, int MaxFps);

/// <summary>Lo que el receptor dice de sí mismo en GET /info.</summary>
public sealed record ReceiverInfo(string? Name, string? Model, string? SourceVersion, IReadOnlyList<ReceiverDisplay> Displays)
{
    public static ReceiverInfo Parse(Dictionary<string, object?> info)
    {
        var displays = new List<ReceiverDisplay>();
        if (info.GetValueOrDefault("displays") is List<object?> list)
        {
            foreach (var display in list.OfType<Dictionary<string, object?>>())
            {
                int Get(string key) => display.GetValueOrDefault(key) is long value ? (int)value : 0;
                int width = Get("widthPixels"), height = Get("heightPixels");
                displays.Add(new ReceiverDisplay(width > 0 ? width : Get("width"), height > 0 ? height : Get("height"),
                    Get("widthPixelsMax"), Get("heightPixelsMax"), Get("maxFPS")));
            }
        }
        return new ReceiverInfo(info.GetValueOrDefault("name") as string, info.GetValueOrDefault("model") as string,
            info.GetValueOrDefault("sourceVersion") as string, displays);
    }
}

/// <summary>Las peticiones de control: GET /info y pair-verify, sobre una conexión.</summary>
public sealed class AirPlayClient(AirPlayConnection connection)
{
    private const string RtspUserAgent = "AirPlay/550.10";
    private const string PairUserAgent = "AirPlay/320.20";
    private const string ControlSalt = "Control-Salt";
    private const string ControlWriteInfo = "Control-Write-Encryption-Key";
    private const string ControlReadInfo = "Control-Read-Encryption-Key";

    private const string PlistContentType = "application/x-apple-binary-plist";

    private readonly string _dacpId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    private readonly string _activeRemote = RandomNumberGenerator.GetInt32(int.MaxValue).ToString(CultureInfo.InvariantCulture);
    private readonly int _sessionId = RandomNumberGenerator.GetInt32(int.MaxValue);
    private int _cseq;

    public AirPlayConnection Connection => connection;

    private string SessionUri => $"rtsp://{connection.LocalAddress}/{_sessionId}";

    /// <summary>La verificación de HomeKit, para derivar las claves de eventos y datos; null en legacy.</summary>
    public HomeKitPairVerify? HomeKit { get; private set; }

    /// <exception cref="IOException">La TV no respondió con un plist.</exception>
    public async Task<ReceiverInfo> GetInfoAsync(CancellationToken ct = default)
    {
        AirPlayResponse response = await RtspAsync("GET", "/info", null, ct);
        if (!response.IsSuccess)
            throw new IOException($"GET /info: {response.Code} {response.Message}");
        if (BinaryPlistReader.Read(response.Body) is not Dictionary<string, object?> info)
            throw new IOException("GET /info: respuesta inesperada");
        return ReceiverInfo.Parse(info);
    }

    /// <summary>Hace pair-verify y, con HomeKit, cifra la conexión.</summary>
    /// <exception cref="PairingException">La TV rechazó las credenciales.</exception>
    public async Task VerifyAsync(Credentials credentials, CancellationToken ct = default)
    {
        if (credentials.Kind == PairingKind.Legacy)
        {
            var legacy = new LegacyPairVerify(credentials);
            byte[] response = await PairVerifyAsync(legacy.Message1(), false, ct);
            await PairVerifyAsync(legacy.Message2(response), false, ct);
            return;
        }

        var verify = new HomeKitPairVerify(credentials);
        byte[] message2 = await PairVerifyAsync(verify.Message1(), true, ct);
        verify.CheckMessage4(await PairVerifyAsync(verify.Message3(message2), true, ct));
        connection.EnableEncryption(verify.DeriveKey(ControlSalt, ControlWriteInfo), verify.DeriveKey(ControlSalt, ControlReadInfo));
        HomeKit = verify;
    }

    /// <summary>SETUP con ese contenido; devuelve el plist de la respuesta.</summary>
    /// <exception cref="IOException">La TV rechazó el SETUP.</exception>
    public async Task<Dictionary<string, object?>> SetupAsync(Dictionary<string, object?> body, CancellationToken ct = default)
    {
        AirPlayResponse response = await RtspAsync("SETUP", null, body, ct);
        if (!response.IsSuccess)
            throw new IOException($"La TV rechazó el SETUP: {response.Code} {response.Message}");
        return response.Body.Length > 0 && BinaryPlistReader.Read(response.Body) is Dictionary<string, object?> plist ? plist : [];
    }

    /// <summary>Otra petición de la sesión (RECORD, TEARDOWN); sin comprobar la respuesta.</summary>
    public Task<AirPlayResponse> SessionRequestAsync(string method, Dictionary<string, object?>? body = null, CancellationToken ct = default) =>
        RtspAsync(method, null, body, ct);

    /// <summary>El aviso periódico de que el emisor sigue vivo.</summary>
    public Task<AirPlayResponse> FeedbackAsync(CancellationToken ct = default) => RtspAsync("POST", "/feedback", null, ct);

    private async Task<byte[]> PairVerifyAsync(byte[] body, bool homeKit, CancellationToken ct)
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("User-Agent", PairUserAgent),
            new("Connection", "keep-alive"),
            new("Content-Type", "application/octet-stream"),
        };
        if (homeKit)
            headers.Add(new("X-Apple-HKP", "3"));
        AirPlayResponse response = await connection.SendAsync("POST /pair-verify HTTP/1.1", headers, body, ct);
        if (!response.IsSuccess)
            throw new PairingException($"La TV rechazó el pair-verify: {response.Code} {response.Message}");
        return response.Body;
    }

    private Task<AirPlayResponse> RtspAsync(string method, string? uri, Dictionary<string, object?>? body, CancellationToken ct)
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("User-Agent", RtspUserAgent),
            new("CSeq", Interlocked.Increment(ref _cseq).ToString(CultureInfo.InvariantCulture)),
            new("DACP-ID", _dacpId),
            new("Active-Remote", _activeRemote),
            new("Client-Instance", _dacpId),
        };
        if (body is not null)
            headers.Add(new("Content-Type", PlistContentType));
        return connection.SendAsync($"{method} {uri ?? SessionUri} RTSP/1.0", headers,
            body is null ? null : BinaryPlistWriter.Write(body), ct);
    }
}
