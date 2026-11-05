// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using AireCast.Core.Net;
using AireCast.Core.Pairing;

namespace AireCast.Core.Mirror;

/// <summary>
/// Duplicado de pantalla por AirPlay 2 con HomeKit (Apple TV modernos, Roku, Samsung, LG…):
///
///   1. SETUP de sesión con isScreenMirroringSession y NTP; la TV consulta la hora y da el puerto de eventos.
///   2. SETUP del flujo de vídeo (tipo 110): la TV da el puerto de datos.
///   3. RECORD, y conexión TCP a ese puerto.
///   4. Cada fotograma: cabecera de 128 bytes y H.264 cifrado con ChaCha20-Poly1305, con la cabecera como AAD.
/// </summary>
public sealed class MirrorSession : IVideoSink, IAsyncDisposable
{
    private const long VideoStreamType = 110;
    private const string EventsSalt = "Events-Salt";
    private const string EventsWriteInfo = "Events-Write-Encryption-Key";
    private const string EventsReadInfo = "Events-Read-Encryption-Key";
    private const string DataSalt = "DataStream-Salt";
    private const string DataOutputInfo = "DataStream-Output-Encryption-Key";
    private static readonly string[] TimestampNames = ["SubSu", "BePxT", "AfPxT", "BefEn", "EmEnc"];
    private static readonly TimeSpan FeedbackInterval = TimeSpan.FromSeconds(2);

    private readonly AirPlayClient _client;
    private readonly TimingServer _timing;
    private readonly CancellationTokenSource _stop = new();
    private EventChannel? _events;
    private TcpClient? _data;
    private NetworkStream? _stream;
    private byte[] _dataKey = [];
    private ulong _frameCounter;
    private Task? _feedback;

    private MirrorSession(AirPlayClient client)
    {
        _client = client;
        _timing = new TimingServer(client.Connection.LocalAddress);
    }

    public int TimingRequests => _timing.Requests;

    /// <summary>Abre la sesión sobre una conexión con pair-verify de HomeKit ya hecho.</summary>
    /// <exception cref="NotSupportedException">Las credenciales son legacy.</exception>
    public static async Task<MirrorSession> StartAsync(AirPlayClient client, string name = "AireCast", CancellationToken ct = default)
    {
        HomeKitPairVerify verify = client.HomeKit
            ?? throw new NotSupportedException("El duplicado legacy (Apple TV antiguos) aún no está en el núcleo");
        var session = new MirrorSession(client);
        try
        {
            await session.SetupAsync(verify, name, ct);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private async Task SetupAsync(HomeKitPairVerify verify, string name, CancellationToken ct)
    {
        string mac = string.Join(':', RandomNumberGenerator.GetBytes(6).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        var session = await _client.SetupAsync(new()
        {
            ["deviceID"] = mac,
            ["macAddress"] = mac,
            ["sessionUUID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
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
            _events = await EventChannel.ConnectAsync(_client.Connection.RemoteAddress, (int)eventPort, channel, ct);
        }

        long streamId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var video = await _client.SetupAsync(new()
        {
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = VideoStreamType,
                    ["streamConnectionID"] = streamId,
                    ["timestampInfo"] = TimestampNames.Select(n => (object?)new Dictionary<string, object?> { ["name"] = n }).ToList(),
                },
            },
        }, ct);
        if (video.GetValueOrDefault("streams") is not List<object?> { Count: > 0 } streams
            || streams[0] is not Dictionary<string, object?> stream || stream.GetValueOrDefault("dataPort") is not long dataPort)
            throw new IOException("La TV no devolvió el puerto de vídeo");

        // Algunas TVs (Roku) lo exigen para empezar a decodificar; otras no lo necesitan y lo rechazan.
        await _client.SessionRequestAsync("RECORD", VideoStreamBody(), ct);

        _dataKey = verify.DeriveKey(DataSalt + streamId.ToString(CultureInfo.InvariantCulture), DataOutputInfo);
        _data = new TcpClient(_client.Connection.RemoteAddress.AddressFamily) { NoDelay = true };
        await _data.ConnectAsync(_client.Connection.RemoteAddress, (int)dataPort, ct);
        _stream = _data.GetStream();
        _feedback = Task.Run(FeedbackLoopAsync);
    }

    private static Dictionary<string, object?> VideoStreamBody() => new()
    {
        ["streams"] = new List<object?> { new Dictionary<string, object?> { ["type"] = VideoStreamType } },
    };

    public async Task SendCodecAsync(byte[] sps, byte[] pps, CancellationToken ct)
    {
        var (width, height) = H264.SpsSize(sps);
        await _stream!.WriteAsync(VideoPackets.Codec(H264.BuildAvcc(sps, pps), width, height, NtpClock.FrameTime()), ct);
    }

    public async Task SendFrameAsync(IReadOnlyList<byte[]> nals, CancellationToken ct)
    {
        const int TagLength = 16;
        byte[] payload = VideoPackets.Avcc(nals);
        byte[] header = VideoPackets.VideoHeader(payload.Length + TagLength, NtpClock.FrameTime());
        byte[] encrypted = PairCrypto.Seal(_dataKey, PairCrypto.CounterNonce(_frameCounter++), payload, header);
        byte[] packet = [.. header, .. encrypted];
        await _stream!.WriteAsync(packet, ct);
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
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // La sesión terminó, o la TV cerró la conexión: lo notará quien envía el vídeo.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_feedback is not null)
            await _feedback;
        if (_stream is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _client.SessionRequestAsync("TEARDOWN", VideoStreamBody(), timeout.Token);
                await _client.SessionRequestAsync("TEARDOWN", null, timeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // La TV ya no contesta: se cierra igual.
            }
        }
        _data?.Dispose();
        _events?.Dispose();
        _timing.Dispose();
        _stop.Dispose();
    }
}
