// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AireCast.Core.Mirror;
using AireCast.Core.Net;
using AireCast.Core.Pairing;
using AireCast.Core.Plist;

namespace AireCast.Core.Tests.Mirror;

/// <summary>
/// TV simulada para probar la sesión completa con sockets reales: acepta el pair-verify, responde al
/// SETUP y al RECORD como una TV con HomeKit, consulta la hora y descifra lo que llega por el canal de datos.
/// </summary>
public sealed class FakeReceiver : IAsyncDisposable
{
    private static readonly byte[] TvSeed = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] ClientSeed = Enumerable.Range(33, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] TvId = "tv"u8.ToArray();
    private static readonly byte[] ClientId = "client"u8.ToArray();
    private static readonly byte[] TvEphemeral = Enumerable.Range(65, 32).Select(i => (byte)i).ToArray();

    private readonly TcpListener _control = new(IPAddress.Loopback, 0);
    private readonly TcpListener _events = new(IPAddress.Loopback, 0);
    private readonly TcpListener _data = new(IPAddress.Loopback, 0);
    private readonly UdpClient _audioData = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly UdpClient _audioControl = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _tasks = [];
    private byte[] _shared = [];
    private long _streamId;

    public static Credentials Credentials { get; } =
        new(PairCrypto.Ed25519PublicKey(TvSeed), ClientSeed, TvId, ClientId);

    public int Port => ((IPEndPoint)_control.LocalEndpoint).Port;
    public ConcurrentQueue<string> Requests { get; } = new();
    public Dictionary<string, object?>? SessionSetup { get; private set; }
    public Dictionary<string, object?>? VideoStream { get; private set; }

    /// <summary>El flujo de audio que pidió el emisor (con su margen, latencyMin y latencyMax), si pidió alguno.</summary>
    public Dictionary<string, object?>? AudioStream { get; private set; }

    /// <summary>La marca de tiempo de cada fotograma, tal como llegó en su cabecera.</summary>
    public ConcurrentQueue<ulong> FrameTimes { get; } = new();

    public bool TimingAnswered { get; private set; }
    public string? EventReply { get; private set; }
    public (int Width, int Height)? CodecSize { get; private set; }
    public ConcurrentQueue<byte[]> Frames { get; } = new();

    public void Start()
    {
        _control.Start();
        _events.Start();
        _data.Start();
        _tasks.Add(Task.Run(ServeControlAsync));
        _tasks.Add(Task.Run(ServeEventsAsync));
        _tasks.Add(Task.Run(ServeDataAsync));
    }

    private async Task ServeControlAsync()
    {
        using TcpClient client = await _control.AcceptTcpClientAsync(_stop.Token);
        NetworkStream stream = client.GetStream();
        HomeKitChannel? channel = null;
        byte[] plain = [];
        byte[] buffer = new byte[8192];
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                    return;
                plain = [.. plain, .. channel is null ? buffer.AsSpan(0, read) : channel.Decrypt(buffer.AsSpan(0, read))];
                while (TakeRequest(ref plain, out string line, out string cseq, out byte[] body))
                {
                    Requests.Enqueue(line.Split(' ')[0] + " " + line.Split(' ')[1].Split('/', 4).Last());
                    var (reply, encryptNext) = await HandleAsync(line, cseq, body);
                    await stream.WriteAsync(channel?.Encrypt(reply) ?? reply, _stop.Token);
                    if (encryptNext)
                        channel = new HomeKitChannel(Key("Control-Salt", "Control-Read-Encryption-Key"), Key("Control-Salt", "Control-Write-Encryption-Key"));
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }
    }

    private async Task<(byte[] Reply, bool EncryptNext)> HandleAsync(string line, string cseq, byte[] body)
    {
        string method = line.Split(' ')[0];
        if (line.StartsWith("POST /pair-verify", StringComparison.Ordinal))
        {
            var tlv = Tlv8.Read(body);
            return tlv[Tlv8.SeqNo][0] == 1 ? (Response("HTTP/1.1", cseq, Message2(tlv[Tlv8.PublicKey])), false) : (Response("HTTP/1.1", cseq, Message4(tlv)), true);
        }
        if (method == "SETUP")
        {
            var plist = (Dictionary<string, object?>)BinaryPlistReader.Read(body)!;
            if (plist.ContainsKey("isScreenMirroringSession"))
            {
                SessionSetup = plist;
                await QueryTimeAsync((int)(long)plist["timingPort"]!);
                return (Plist(cseq, new() { ["eventPort"] = (long)((IPEndPoint)_events.LocalEndpoint).Port }), false);
            }
            var requested = (Dictionary<string, object?>)((List<object?>)plist["streams"]!)[0]!;
            if ((long)requested["type"]! == 96)
            {
                AudioStream = requested;
                var audio = new Dictionary<string, object?>
                {
                    ["type"] = 96L,
                    ["dataPort"] = (long)((IPEndPoint)_audioData.Client.LocalEndPoint!).Port,
                    ["controlPort"] = (long)((IPEndPoint)_audioControl.Client.LocalEndPoint!).Port,
                };
                return (Plist(cseq, new() { ["streams"] = new List<object?> { audio } }), false);
            }
            VideoStream = requested;
            _streamId = (long)VideoStream["streamConnectionID"]!;
            var stream = new Dictionary<string, object?> { ["type"] = 110L, ["dataPort"] = (long)((IPEndPoint)_data.LocalEndpoint).Port };
            return (Plist(cseq, new() { ["streams"] = new List<object?> { stream } }), false);
        }
        return (Response("RTSP/1.0", cseq, []), false);
    }

    private byte[] Message2(byte[] clientPublic)
    {
        _clientPublic = clientPublic;
        _shared = PairCrypto.X25519Shared(TvEphemeral, clientPublic);
        byte[] tvPublic = PairCrypto.X25519PublicKey(TvEphemeral);
        byte[] signature = PairCrypto.Sign(TvSeed, [.. tvPublic, .. TvId, .. clientPublic]);
        byte[] inner = Tlv8.Write((Tlv8.Identifier, TvId), (Tlv8.Signature, signature));
        byte[] encrypted = PairCrypto.Seal(VerifyKey(), PairCrypto.TextNonce("PV-Msg02"), inner);
        return Tlv8.Write((Tlv8.SeqNo, [2]), (Tlv8.PublicKey, tvPublic), (Tlv8.EncryptedData, encrypted));
    }

    private byte[] _clientPublic = [];

    private byte[] Message4(Dictionary<byte, byte[]> tlv)
    {
        var inner = Tlv8.Read(PairCrypto.Open(VerifyKey(), PairCrypto.TextNonce("PV-Msg03"), tlv[Tlv8.EncryptedData]));
        byte[] tvPublic = PairCrypto.X25519PublicKey(TvEphemeral);
        bool valid = PairCrypto.VerifySignature(PairCrypto.Ed25519PublicKey(ClientSeed),
            [.. _clientPublic, .. inner[Tlv8.Identifier], .. tvPublic], inner[Tlv8.Signature]);
        return valid ? Tlv8.Write((Tlv8.SeqNo, [4])) : Tlv8.Write((Tlv8.Error, [2]));
    }

    private byte[] VerifyKey() => Key("Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info");

    private byte[] Key(string salt, string info) => PairCrypto.Hkdf(salt, info, _shared);

    private async Task QueryTimeAsync(int port)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] query = new byte[32];
        query[0] = 0x80;
        query[1] = 0xD2;
        await udp.SendAsync(query, new IPEndPoint(IPAddress.Loopback, port), _stop.Token);
        var reply = await udp.ReceiveAsync(_stop.Token);
        TimingAnswered = reply.Buffer.Length == 32 && reply.Buffer[1] == 0xD3;
    }

    private async Task ServeEventsAsync()
    {
        using TcpClient client = await _events.AcceptTcpClientAsync(_stop.Token);
        NetworkStream stream = client.GetStream();
        var channel = new HomeKitChannel(Key("Events-Salt", "Events-Write-Encryption-Key"), Key("Events-Salt", "Events-Read-Encryption-Key"));
        await stream.WriteAsync(channel.Encrypt("POST /command RTSP/1.0\r\nCSeq: 1\r\nContent-Length: 0\r\n\r\n"u8), _stop.Token);
        byte[] buffer = new byte[1024];
        int read = await stream.ReadAsync(buffer, _stop.Token);
        EventReply = Encoding.ASCII.GetString(channel.Decrypt(buffer.AsSpan(0, read)));
    }

    private async Task ServeDataAsync()
    {
        using TcpClient client = await _data.AcceptTcpClientAsync(_stop.Token);
        NetworkStream stream = client.GetStream();
        byte[] key = Key("DataStream-Salt" + _streamId.ToString(CultureInfo.InvariantCulture), "DataStream-Output-Encryption-Key");
        byte[] header = new byte[VideoPackets.HeaderLength];
        ulong counter = 0;
        try
        {
            while (true)
            {
                await stream.ReadExactlyAsync(header, _stop.Token);
                byte[] payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
                await stream.ReadExactlyAsync(payload, _stop.Token);
                if (header[4] == 1)
                    CodecSize = ((int)BinaryPrimitives.ReadSingleLittleEndian(header.AsSpan(16)), (int)BinaryPrimitives.ReadSingleLittleEndian(header.AsSpan(20)));
                else
                {
                    FrameTimes.Enqueue(BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8)));
                    Frames.Enqueue(PairCrypto.Open(key, PairCrypto.CounterNonce(counter++), payload, header));
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or EndOfStreamException)
        {
        }
    }

    private static bool TakeRequest(ref byte[] buffer, out string line, out string cseq, out byte[] body)
    {
        line = cseq = "";
        body = [];
        int end = buffer.AsSpan().IndexOf("\r\n\r\n"u8);
        if (end < 0)
            return false;
        string[] lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
        int length = 0;
        foreach (string header in lines.Skip(1))
        {
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(header.Split(':')[1], CultureInfo.InvariantCulture);
            else if (header.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase))
                cseq = header.Split(':')[1].Trim();
        }
        if (buffer.Length < end + 4 + length)
            return false;
        line = lines[0];
        body = buffer[(end + 4)..(end + 4 + length)];
        buffer = buffer[(end + 4 + length)..];
        return true;
    }

    private static byte[] Plist(string cseq, Dictionary<string, object?> plist) =>
        Response("RTSP/1.0", cseq, BinaryPlistWriter.Write(plist));

    private static byte[] Response(string protocol, string cseq, byte[] body) =>
        [.. Encoding.ASCII.GetBytes($"{protocol} 200 OK\r\nCSeq: {cseq}\r\nContent-Length: {body.Length}\r\n\r\n"), .. body];

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _control.Stop();
        _events.Stop();
        _data.Stop();
        await Task.WhenAll(_tasks.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
        _audioData.Dispose();
        _audioControl.Dispose();
        _stop.Dispose();
    }
}
