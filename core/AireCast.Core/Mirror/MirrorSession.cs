// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AireCast.Core.Audio;
using AireCast.Core.FairPlay;
using AireCast.Core.Net;
using AireCast.Core.Pairing;

namespace AireCast.Core.Mirror;

/// <summary>
/// Sesión de duplicado de pantalla. Hay dos caminos, según las credenciales:
///
/// HomeKit (Apple TV modernos, Roku, Samsung, LG…):
///   1. SETUP de sesión con isScreenMirroringSession y NTP; la TV consulta la hora y da el puerto de eventos.
///   2. SETUP de vídeo (tipo 110) y de audio (tipo 96): la TV da los puertos.
///   3. RECORD y conexión TCP al puerto de datos.
///   4. Cada fotograma: cabecera de 128 bytes y H.264 con ChaCha20-Poly1305, con la cabecera como AAD.
///
/// Legacy (Apple TV antiguos):
///   1. FairPlay SAP en POST /fp-setup.
///   2. SETUP «media-first»: sesión, flujo de audio y clave AES envuelta (ekey/eiv).
///   3. SETUP de vídeo con la clave en shk/shiv, conexión al puerto de datos y RECORD.
///   4. Vídeo con AES-128-CTR (clave e IV derivados con SHA-512) y un pulso por segundo.
/// Si la TV responde 401 («código en pantalla»), pide el código y reintenta con Digest.
/// </summary>
public sealed class MirrorSession : IVideoSink, IAsyncDisposable
{
    private const long VideoStreamType = 110;
    private const long AudioStreamType = 96;
    private const string EventsSalt = "Events-Salt";
    private const string EventsWriteInfo = "Events-Write-Encryption-Key";
    private const string EventsReadInfo = "Events-Read-Encryption-Key";
    private const string DataSalt = "DataStream-Salt";
    private const string DataOutputInfo = "DataStream-Output-Encryption-Key";
    private const byte FrameIdr = 0x10;
    private const int TagLength = 16;

    /// <summary>
    /// Búfer que pedimos al receptor legacy. Medido con el Apple TV 3: 25 ms corta el audio, 50 ms tiene
    /// pequeños fallos y 75 ms es estable; por encima de ~100 ms la TV no lo respeta en el vídeo.
    /// </summary>
    private const int LegacyVideoLatencyMs = 65;
    private const int LegacyAudioLeadMs = 10; // el audio va por delante del vídeo, como doubletake

    private static readonly string[] TimestampNames = ["SubSu", "BePxT", "AfPxT", "BefEn", "EmEnc"];
    private static readonly TimeSpan FeedbackInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    private readonly AirPlayClient _client;
    private readonly TimingServer _timing;
    private readonly PcmBuffer? _pcm;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly List<Task> _loops = [];
    private readonly string _mac = string.Join(':', RandomNumberGenerator.GetBytes(6).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    private readonly long _streamId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
    private EventChannel? _events;
    private TcpClient? _data;
    private NetworkStream? _stream;
    private byte[] _dataKey = [];
    private ulong _frameCounter;
    private AesCtr? _legacyCipher;
    private AudioStream? _audio;
    private bool _frameSent;

    private MirrorSession(AirPlayClient client, PcmBuffer? pcm)
    {
        _client = client;
        _pcm = pcm;
        _timing = new TimingServer(client.Connection.LocalAddress);
    }

    public int TimingRequests => _timing.Requests;

    /// <summary>Cuántos paquetes de audio pidió reenviar la TV.</summary>
    public int ResentAudioPackets => _audio?.ResentPackets ?? 0;

    /// <summary>Si la TV aceptó el flujo de audio.</summary>
    public bool HasAudio => _audio is not null;

    /// <summary>
    /// Abre la sesión sobre una conexión con pair-verify ya hecho. Con <paramref name="pcm"/> también
    /// envía audio; si la TV no lo acepta, sigue solo con vídeo.
    /// </summary>
    public static async Task<MirrorSession> StartAsync(AirPlayClient client, PcmBuffer? pcm = null, string name = "AireCast", CancellationToken ct = default)
    {
        var session = new MirrorSession(client, pcm);
        try
        {
            if (client.HomeKit is { } verify)
                await session.SetupHomeKitAsync(verify, name, ct);
            else
                await session.SetupLegacyAsync(name, ct);
            session._audio?.Start();
            session._loops.Add(Task.Run(session.FeedbackLoopAsync));
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private async Task SetupHomeKitAsync(HomeKitPairVerify verify, string name, CancellationToken ct)
    {
        var session = await _client.SetupAsync(new()
        {
            ["deviceID"] = _mac,
            ["macAddress"] = _mac,
            ["sessionUUID"] = _client.SessionUuid,
            ["isScreenMirroringSession"] = true,
            ["name"] = name,
            ["statsCollectionEnabled"] = false,
            ["timingPort"] = (long)_timing.Port,
            ["timingProtocol"] = "NTP",
            ["model"] = "iPad5,3",
            ["osName"] = "iPhone OS",
            ["osVersion"] = "15.8.2",
            ["osBuildVersion"] = "19H384",
            ["sourceVersion"] = "620.8.2",
        }, ct);

        if (session.GetValueOrDefault("eventPort") is long eventPort)
        {
            // La TV escribe con la clave de «write»: desde aquí se usan al revés.
            var channel = new HomeKitChannel(verify.DeriveKey(EventsSalt, EventsReadInfo), verify.DeriveKey(EventsSalt, EventsWriteInfo));
            _events = await EventChannel.ConnectAsync(RemoteAddress, (int)eventPort, channel, ct);
        }

        long dataPort = VideoDataPort(await _client.SetupAsync(new()
        {
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = VideoStreamType,
                    ["streamConnectionID"] = _streamId,
                    ["timestampInfo"] = TimestampInfo(),
                },
            },
        }, ct));

        if (_pcm is not null)
            await SetupHomeKitAudioAsync(ct);

        // Algunas TVs (Roku) lo exigen para empezar a decodificar; otras lo ignoran.
        await _client.SessionRequestAsync("RECORD", VideoStreamBody(), ct: ct);

        _dataKey = verify.DeriveKey(DataSalt + _streamId.ToString(CultureInfo.InvariantCulture), DataOutputInfo);
        await ConnectDataAsync(dataPort, ct);
    }

    private async Task SetupHomeKitAudioAsync(CancellationToken ct)
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        uint ssrc = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        Socket control = Ports.BindUdp(_client.Connection.LocalAddress);
        try
        {
            var response = await _client.SetupAsync(new()
            {
                ["streams"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = AudioStreamType,
                        ["ct"] = 1L, // PCM sin comprimir (16 bits big-endian)
                        ["audioFormat"] = 0x800L, // PCM 44100/16/2
                        ["audioMode"] = "default",
                        ["spf"] = (long)AudioFormat.SamplesPerPacket,
                        ["sr"] = (long)AudioFormat.Rate,
                        ["shk"] = key,
                        ["controlPort"] = (long)((IPEndPoint)control.LocalEndPoint!).Port,
                        ["isMedia"] = true,
                        ["usingScreen"] = true,
                        ["latencyMin"] = (long)AudioFormat.DefaultLatency,
                        ["latencyMax"] = (long)AudioFormat.MaxLatency,
                        ["supportsDynamicStreamID"] = false,
                        ["streamConnectionID"] = (long)ssrc,
                    },
                },
            }, ct);
            if (StreamOfType(response, AudioStreamType) is not { } stream || stream.GetValueOrDefault("dataPort") is not long dataPort)
                throw new IOException("La TV no devolvió el puerto de audio");

            // Si la TV declara cuánto tarda en reproducir lo que le llega (Apple TV 4K: 27 ms), con solo
            // el margen por defecto casi todo llega tarde y suena robótico.
            int latency = stream.GetValueOrDefault("arrivalToRenderLatencyMs") is long arrival
                ? Math.Min(AudioFormat.Rate * ((int)arrival + AudioFormat.NetworkMarginMs) / 1000, AudioFormat.MaxLatency)
                : AudioFormat.DefaultLatency;
            _audio = NewAudioStream(control, dataPort, stream, new ModernAudioPacketizer(key, ssrc), latency);
        }
        catch (IOException)
        {
            control.Dispose(); // si la TV rechaza el audio, se sigue solo con vídeo
        }
    }

    private async Task SetupLegacyAsync(string name, CancellationToken ct)
    {
        // 1. FairPlay SAP: m1 -> m2, m3 -> m4. Después la clave AES del vídeo viaja envuelta (ekey).
        var fairPlay = new FairPlaySession();
        byte[] message2 = await _client.FairPlaySetupAsync(FairPlaySession.Message1(), ct);
        fairPlay.Confirm(await _client.FairPlaySetupAsync(fairPlay.Message3(message2), ct));
        byte[] key = RandomNumberGenerator.GetBytes(16);
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        int audioLatency = AudioFormat.Rate * (LegacyVideoLatencyMs + LegacyAudioLeadMs) / 1000;

        // 2. SETUP media-first: crea la sesión junto con el flujo de audio. El receptor lo exige aunque
        //    no se vaya a enviar audio.
        Socket control = Ports.BindUdp(_client.Connection.LocalAddress);
        try
        {
            var session = await _client.SetupAsync(new()
            {
                ["deviceID"] = _mac,
                ["macAddress"] = _mac,
                ["sessionUUID"] = _client.SessionUuid,
                ["isScreenMirroringSession"] = true,
                ["name"] = name,
                ["timingPort"] = (long)_timing.Port,
                ["timingProtocol"] = "NTP",
                ["model"] = "Linux",
                ["osBuildVersion"] = "13F69",
                ["sourceVersion"] = "280.33",
                ["ekey"] = fairPlay.WrapKey(key),
                ["eiv"] = iv,
                ["et"] = 32L,
                ["streams"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = AudioStreamType,
                        ["streamConnectionID"] = RandomNumberGenerator.GetInt32(1, int.MaxValue) * 2L,
                        ["ct"] = 2L, // ALAC
                        ["spf"] = (long)AudioFormat.SamplesPerPacket,
                        ["sr"] = (long)AudioFormat.Rate,
                        ["audioFormat"] = 0x40000L,
                        ["audioMode"] = "default",
                        ["usingScreen"] = true,
                        ["latencyMin"] = 0L,
                        ["latencyMax"] = (long)audioLatency,
                        ["controlPort"] = (long)((IPEndPoint)control.LocalEndPoint!).Port,
                    },
                },
            }, ct);

            if (_pcm is not null && StreamOfType(session, AudioStreamType) is { } stream && stream.GetValueOrDefault("dataPort") is long audioPort)
                _audio = NewAudioStream(control, audioPort, stream, new LegacyAudioPacketizer(key, iv), audioLatency);
            else
                control.Dispose();

            if (session.GetValueOrDefault("eventPort") is long eventPort)
                _events = await EventChannel.ConnectAsync(RemoteAddress, (int)eventPort, null, ct);
        }
        catch
        {
            if (_audio is null)
                control.Dispose();
            throw;
        }

        // 3. SETUP de vídeo con la clave en claro -> puerto de datos; RECORD.
        long dataPort = VideoDataPort(await _client.SetupAsync(new()
        {
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = VideoStreamType,
                    ["streamConnectionID"] = _streamId,
                    ["latencyMs"] = (long)LegacyVideoLatencyMs,
                    ["timestampInfo"] = TimestampInfo(),
                    ["shk"] = key,
                    ["shiv"] = iv,
                },
            },
        }, ct));
        await ConnectDataAsync(dataPort, ct);
        AirPlayResponse record = await _client.SessionRequestAsync("RECORD", null,
        [
            new("Session", _client.SessionUuid),
            new("Range", "npt=0-"),
            new("RTP-Info", "seq=0;rtptime=0"),
        ], ct);
        if (record.Code >= 400)
            throw new IOException($"La TV rechazó el RECORD: {record.Code} {record.Message}");

        var (videoKey, videoIv) = DeriveLegacyVideoKeys(key, _streamId);
        _legacyCipher = new AesCtr(videoKey, videoIv);
        _loops.Add(Task.Run(HeartbeatLoopAsync));
        if (_audio is not null)
            await _client.SetFullVolumeAsync(ct);
    }

    /// <summary>Clave e IV de AES-CTR del vídeo legacy: SHA-512("AirPlayStream{Key,IV}&lt;id&gt;" + clave)[:16].</summary>
    public static (byte[] Key, byte[] Iv) DeriveLegacyVideoKeys(byte[] key, long streamId)
    {
        byte[] Derive(string label) => SHA512.HashData([.. Encoding.ASCII.GetBytes(label + streamId.ToString(CultureInfo.InvariantCulture)), .. key])[..16];

        return (Derive("AirPlayStreamKey"), Derive("AirPlayStreamIV"));
    }

    private IPAddress RemoteAddress => _client.Connection.RemoteAddress;

    private AudioStream NewAudioStream(Socket control, long dataPort, Dictionary<string, object?> stream, IAudioPacketizer packetizer, int latency) =>
        new(control, new IPEndPoint(RemoteAddress, (int)dataPort),
            stream.GetValueOrDefault("controlPort") is long port ? new IPEndPoint(RemoteAddress, (int)port) : null,
            packetizer, _pcm!, latency);

    private static List<object?> TimestampInfo() =>
        TimestampNames.Select(name => (object?)new Dictionary<string, object?> { ["name"] = name }).ToList();

    private static Dictionary<string, object?>? StreamOfType(Dictionary<string, object?> response, long type) =>
        (response.GetValueOrDefault("streams") as List<object?>)?.OfType<Dictionary<string, object?>>()
            .FirstOrDefault(stream => stream.GetValueOrDefault("type") is long value && value == type);

    private static long VideoDataPort(Dictionary<string, object?> response) =>
        StreamOfType(response, VideoStreamType)?.GetValueOrDefault("dataPort") as long?
            ?? throw new IOException("La TV no devolvió el puerto de vídeo");

    private async Task ConnectDataAsync(long port, CancellationToken ct)
    {
        _data = new TcpClient(RemoteAddress.AddressFamily) { NoDelay = true };
        await _data.ConnectAsync(RemoteAddress, (int)port, ct);
        _stream = _data.GetStream();
    }

    private static Dictionary<string, object?> VideoStreamBody() => new()
    {
        ["streams"] = new List<object?> { new Dictionary<string, object?> { ["type"] = VideoStreamType } },
    };

    public async Task SendCodecAsync(byte[] sps, byte[] pps, CancellationToken ct)
    {
        var (width, height) = H264.SpsSize(sps);
        await WritePacketAsync(VideoPackets.Codec(H264.BuildAvcc(sps, pps), width, height, NtpClock.FrameTime()), ct);
    }

    public async Task SendFrameAsync(IReadOnlyList<byte[]> nals, CancellationToken ct)
    {
        byte[] payload = VideoPackets.Avcc(nals);
        byte[] packet;
        if (_legacyCipher is not null)
        {
            byte flags = nals.Any(nal => H264.NalType(nal) == H264.NalIdr) ? FrameIdr : (byte)0;
            byte[] header = VideoPackets.Header(payload.Length, 0, flags, 0, NtpClock.FrameTime());
            packet = [.. header, .. _legacyCipher.Transform(payload)];
        }
        else
        {
            byte[] header = VideoPackets.VideoHeader(payload.Length + TagLength, NtpClock.FrameTime());
            packet = [.. header, .. PairCrypto.Seal(_dataKey, PairCrypto.CounterNonce(_frameCounter++), payload, header)];
        }
        await WritePacketAsync(packet, ct);
        _frameSent = true;
    }

    /// <summary>Cada paquete se escribe entero: el pulso nunca queda en medio de un fotograma.</summary>
    private async Task WritePacketAsync(byte[] packet, CancellationToken ct)
    {
        await _writing.WaitAsync(ct);
        try
        {
            await _stream!.WriteAsync(packet, ct);
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <summary>Pulso del canal de vídeo para que el Apple TV no lo dé por inactivo (salvapantallas).</summary>
    private async Task HeartbeatLoopAsync()
    {
        byte[] heartbeat = VideoPackets.Header(0, VideoPackets.TypeHeartbeat, 0, VideoPackets.HeartbeatOption, 0);
        try
        {
            while (true)
            {
                await Task.Delay(HeartbeatInterval, _stop.Token);
                if (_frameSent)
                    await WritePacketAsync(heartbeat, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Terminó la sesión.
        }
    }

    private async Task FeedbackLoopAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(FeedbackInterval, _stop.Token);
                await _client.FeedbackAsync(_stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // La sesión terminó, o la TV cerró la conexión: lo notará quien envía el vídeo.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await Task.WhenAll(_loops);
        bool hadAudio = _audio is not null;
        _audio?.Dispose();
        if (_stream is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (hadAudio)
                    await _client.SessionRequestAsync("TEARDOWN", StreamBody(AudioStreamType), ct: timeout.Token);
                await _client.SessionRequestAsync("TEARDOWN", VideoStreamBody(), ct: timeout.Token);
                await _client.SessionRequestAsync("TEARDOWN", null, ct: timeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // La TV ya no contesta: se cierra igual.
            }
        }
        _data?.Dispose();
        _events?.Dispose();
        _legacyCipher?.Dispose();
        _timing.Dispose();
        _writing.Dispose();
        _stop.Dispose();
    }

    private static Dictionary<string, object?> StreamBody(long type) => new()
    {
        ["streams"] = new List<object?> { new Dictionary<string, object?> { ["type"] = type } },
    };
}
