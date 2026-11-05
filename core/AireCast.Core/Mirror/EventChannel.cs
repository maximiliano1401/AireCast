// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using AireCast.Core.Net;

namespace AireCast.Core.Mirror;

/// <summary>
/// Canal de eventos: la TV manda peticiones (cambios de estado, avisos) y espera un 200 a cada una.
/// Si no se responde, corta la sesión.
/// </summary>
public sealed class EventChannel : IDisposable
{
    private static readonly byte[] HeaderEnd = "\r\n\r\n"u8.ToArray();

    private readonly TcpClient _client;
    private readonly HomeKitChannel _channel;
    private readonly CancellationTokenSource _stop = new();
    private byte[] _buffer = [];

    private EventChannel(TcpClient client, HomeKitChannel channel)
    {
        _client = client;
        _channel = channel;
    }

    public static async Task<EventChannel> ConnectAsync(IPAddress address, int port, HomeKitChannel channel, CancellationToken ct)
    {
        var client = new TcpClient(address.AddressFamily) { NoDelay = true };
        await client.ConnectAsync(address, port, ct);
        var events = new EventChannel(client, channel);
        _ = Task.Run(events.ServeAsync);
        return events;
    }

    private async Task ServeAsync()
    {
        NetworkStream stream = _client.GetStream();
        byte[] buffer = new byte[8192];
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                    return;
                _buffer = [.. _buffer, .. _channel.Decrypt(buffer.AsSpan(0, read))];
                while (TakeRequest(ref _buffer) is { } reply)
                    await stream.WriteAsync(_channel.Encrypt(reply), _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Al cerrar la sesión se corta el canal.
        }
    }

    /// <summary>Saca una petición completa del búfer y devuelve la respuesta, o null si aún falta algo.</summary>
    public static byte[]? TakeRequest(ref byte[] buffer)
    {
        int end = buffer.AsSpan().IndexOf(HeaderEnd);
        if (end < 0)
            return null;
        string[] lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
        var headers = lines.Skip(1).Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
        int length = headers.TryGetValue("Content-Length", out string? value) && int.TryParse(value, out int parsed) ? parsed : 0;
        int total = end + HeaderEnd.Length + length;
        if (buffer.Length < total)
            return null;
        buffer = buffer[total..];

        var reply = new StringBuilder(lines[0].Split(' ')[^1]).Append(" 200 OK\r\nContent-Length: 0\r\nAudio-Latency: 0\r\n");
        if (headers.TryGetValue("CSeq", out string? cseq))
            reply.Append("CSeq: ").Append(cseq).Append("\r\n");
        if (headers.TryGetValue("Server", out string? server))
            reply.Append("Server: ").Append(server).Append("\r\n");
        return Encoding.ASCII.GetBytes(reply.Append("\r\n").ToString());
    }

    public void Dispose()
    {
        _stop.Cancel();
        _client.Dispose();
        _stop.Dispose();
    }
}
