// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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

/// <summary>Las peticiones de control sobre una conexión: GET /info, pair-verify, FairPlay y la sesión RTSP.</summary>
public sealed partial class AirPlayClient(AirPlayConnection connection)
{
    private const int MaxCodeAttempts = 3;
    private const string DigestUser = "AirPlay";

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
    private (string Realm, string Password, string Nonce)? _digest;

    public AirPlayConnection Connection => connection;

    /// <summary>
    /// Pide el código que muestra la TV (Apple TV en modo «código en pantalla»). El argumento dice
    /// si es un reintento porque el anterior era incorrecto.
    /// </summary>
    public Func<bool, CancellationToken, Task<string>>? AskCode { get; set; }

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
        AirPlayResponse response = await AuthorizedAsync(() => RtspAsync("SETUP", null, body, ct), ct);
        if (!response.IsSuccess)
            throw new IOException($"La TV rechazó el SETUP: {response.Code} {response.Message}");
        return response.Body.Length > 0 && BinaryPlistReader.Read(response.Body) is Dictionary<string, object?> plist ? plist : [];
    }

    /// <summary>Otra petición de la sesión (RECORD, TEARDOWN); sin comprobar la respuesta.</summary>
    public Task<AirPlayResponse> SessionRequestAsync(string method, Dictionary<string, object?>? body = null,
        IEnumerable<KeyValuePair<string, string>>? headers = null, CancellationToken ct = default) =>
        AuthorizedAsync(() => RtspAsync(method, null, body, ct, headers), ct);

    /// <summary>Fija el volumen en 0 dB, el máximo de AirPlay: el control queda en el mando de la TV.</summary>
    public Task<AirPlayResponse> SetFullVolumeAsync(CancellationToken ct = default) =>
        AuthorizedAsync(() => RtspAsync("SET_PARAMETER", null, null, ct, null, "volume: 0.000000\r\n"u8.ToArray(), "text/parameters"), ct);

    /// <summary>Un mensaje del intercambio FairPlay (POST /fp-setup).</summary>
    /// <exception cref="IOException">La TV lo rechazó.</exception>
    public async Task<byte[]> FairPlaySetupAsync(byte[] message, CancellationToken ct = default)
    {
        AirPlayResponse response = await AuthorizedAsync(() =>
        {
            var headers = new List<KeyValuePair<string, string>>
            {
                new("Content-Type", "application/octet-stream"),
                new("X-Apple-ET", "32"),
            };
            if (DigestHeader("POST", "/fp-setup") is { } authorization)
                headers.Add(new("Authorization", authorization));
            return connection.SendAsync("POST /fp-setup HTTP/1.1", headers, message, ct);
        }, ct);
        if (!response.IsSuccess)
            throw new IOException($"La TV rechazó FairPlay (/fp-setup): {response.Code} {response.Message}");
        return response.Body;
    }

    /// <summary>Hace la petición y, si la TV responde 401, pide el código que muestra y reintenta con Digest.</summary>
    private async Task<AirPlayResponse> AuthorizedAsync(Func<Task<AirPlayResponse>> send, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            AirPlayResponse response = await send();
            if (response.Code != 401)
                return response;
            if (attempt == MaxCodeAttempts)
                throw new IOException("Código de AirPlay incorrecto");
            if (AskCode is null)
                throw new IOException("La TV pide el código de AirPlay que muestra en pantalla");

            string challenge = response.Headers.GetValueOrDefault("WWW-Authenticate") ?? "";
            var values = ChallengeValue().Matches(challenge).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
            string code = await AskCode(attempt > 0, ct);
            _digest = (values.GetValueOrDefault("realm", "airplay"), code, values.GetValueOrDefault("nonce", ""));
        }
    }

    /// <summary>Cabecera Authorization de Digest, o null si la TV no la ha pedido.</summary>
    private string? DigestHeader(string method, string uri) =>
        _digest is var (realm, password, nonce) ? BuildDigest(method, uri, realm, password, nonce) : null;

    internal static string BuildDigest(string method, string uri, string realm, string password, string nonce)
    {
        static string Md5(string text) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));

        string response = Md5($"{Md5($"{DigestUser}:{realm}:{password}")}:{nonce}:{Md5($"{method}:{uri}")}");
        return $"Digest username=\"{DigestUser}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{uri}\", response=\"{response}\"";
    }

    [GeneratedRegex(@"(\w+)=""([^""]*)""")]
    private static partial Regex ChallengeValue();

    /// <summary>El aviso periódico de que el emisor sigue vivo.</summary>
    public Task<AirPlayResponse> FeedbackAsync(CancellationToken ct = default) => RtspAsync("POST", "/feedback", null, ct);

    /// <summary>El sessionUUID de la sesión: va en el SETUP y en la cabecera Session del RECORD legacy.</summary>
    public string SessionUuid { get; } = Guid.NewGuid().ToString().ToUpperInvariant();

    /// <summary>Hace que la TV muestre el PIN de emparejamiento.</summary>
    public async Task ShowPinAsync(bool homeKit, CancellationToken ct = default) =>
        await PairRequestAsync("/pair-pin-start", null, homeKit, "application/octet-stream", ct);

    /// <summary>Paso de pair-setup de HomeKit: envía TLV8 y devuelve la respuesta.</summary>
    public Task<byte[]> PairSetupAsync(byte[] body, CancellationToken ct = default) =>
        PairRequestAsync("/pair-setup", body, true, "application/octet-stream", ct);

    /// <summary>Paso de pair-setup legacy: envía un plist y devuelve la respuesta, si la hay.</summary>
    public async Task<Dictionary<string, object?>> PairSetupPinAsync(Dictionary<string, object?> body, CancellationToken ct = default)
    {
        byte[] response = await PairRequestAsync("/pair-setup-pin", BinaryPlistWriter.Write(body), false, PlistContentType, ct);
        return BinaryPlistReader.IsBinaryPlist(response) && BinaryPlistReader.Read(response) is Dictionary<string, object?> plist ? plist : [];
    }

    private async Task<byte[]> PairRequestAsync(string path, byte[]? body, bool homeKit, string contentType, CancellationToken ct)
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("User-Agent", PairUserAgent),
            new("Connection", "keep-alive"),
            new("Content-Type", contentType),
        };
        if (homeKit)
            headers.Add(new("X-Apple-HKP", "3"));
        AirPlayResponse response = await connection.SendAsync($"POST {path} HTTP/1.1", headers, body, ct);
        if (!response.IsSuccess)
            throw new PairingException($"La TV rechazó el emparejamiento: {response.Code} {response.Message}");
        return response.Body;
    }

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

    private Task<AirPlayResponse> RtspAsync(string method, string? uri, Dictionary<string, object?>? body, CancellationToken ct,
        IEnumerable<KeyValuePair<string, string>>? extraHeaders = null, byte[]? rawBody = null, string? contentType = null)
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("User-Agent", RtspUserAgent),
            new("CSeq", Interlocked.Increment(ref _cseq).ToString(CultureInfo.InvariantCulture)),
            new("DACP-ID", _dacpId),
            new("Active-Remote", _activeRemote),
            new("Client-Instance", _dacpId),
        };
        if (DigestHeader(method, uri ?? SessionUri) is { } authorization)
            headers.Add(new("Authorization", authorization));
        if (extraHeaders is not null)
            headers.AddRange(extraHeaders);
        if (body is not null || contentType is not null)
            headers.Add(new("Content-Type", contentType ?? PlistContentType));
        return connection.SendAsync($"{method} {uri ?? SessionUri} RTSP/1.0", headers,
            rawBody ?? (body is null ? null : BinaryPlistWriter.Write(body)), ct);
    }
}
