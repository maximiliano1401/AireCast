// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AireCast.Core.Net;

public sealed record AirPlayResponse(int Code, string Message, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public bool IsSuccess => Code is >= 200 and < 300;
}

/// <summary>
/// Conexión TCP de control con el receptor: peticiones HTTP/RTSP de una en una. Tras el
/// pair-verify de HomeKit todo el tráfico va cifrado.
/// </summary>
public sealed class AirPlayConnection : IDisposable
{
    private const int MaxResponseLength = 16 * 1024 * 1024;
    private static readonly byte[] HeaderEnd = "\r\n\r\n"u8.ToArray();

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<byte> _received = [];
    private HomeKitChannel? _channel;

    private AirPlayConnection(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public IPAddress LocalAddress => ((IPEndPoint)_client.Client.LocalEndPoint!).Address;
    public IPAddress RemoteAddress => ((IPEndPoint)_client.Client.RemoteEndPoint!).Address;

    public static async Task<AirPlayConnection> ConnectAsync(IPAddress address, int port, CancellationToken ct = default)
    {
        var client = new TcpClient(address.AddressFamily) { NoDelay = true };
        try
        {
            await client.ConnectAsync(address, port, ct);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return new AirPlayConnection(client);
    }

    public void EnableEncryption(byte[] outputKey, byte[] inputKey) => _channel = new HomeKitChannel(outputKey, inputKey);

    /// <summary>Envía una petición y espera su respuesta.</summary>
    /// <exception cref="IOException">La TV cerró la conexión o respondió algo ilegible.</exception>
    public async Task<AirPlayResponse> SendAsync(string requestLine, IEnumerable<KeyValuePair<string, string>> headers,
        byte[]? body = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var head = new StringBuilder(requestLine).Append("\r\n");
            foreach (var (name, value) in headers)
                head.Append(name).Append(": ").Append(value).Append("\r\n");
            head.Append("Content-Length: ").Append(body?.Length ?? 0).Append("\r\n\r\n");

            byte[] request = [.. Encoding.ASCII.GetBytes(head.ToString()), .. body ?? []];
            await _stream.WriteAsync(_channel?.Encrypt(request) ?? request, ct);
            return await ReadResponseAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<AirPlayResponse> ReadResponseAsync(CancellationToken ct)
    {
        byte[] buffer = new byte[8192];
        while (true)
        {
            if (TryParse(out AirPlayResponse? response))
                return response;
            int read = await _stream.ReadAsync(buffer, ct);
            if (read == 0)
                throw new IOException("La TV cerró la conexión");
            _received.AddRange(_channel is null ? buffer.AsSpan(0, read) : _channel.Decrypt(buffer.AsSpan(0, read)));
            if (_received.Count > MaxResponseLength)
                throw new IOException("Respuesta de la TV demasiado grande");
        }
    }

    private bool TryParse(out AirPlayResponse response)
    {
        response = null!;
        byte[] data = [.. _received];
        int end = data.AsSpan().IndexOf(HeaderEnd);
        if (end < 0)
            return false;

        string[] lines = Encoding.ASCII.GetString(data, 0, end).Split("\r\n");
        var status = lines[0].Split(' ', 3);
        if (status.Length < 2 || !int.TryParse(status[1], NumberStyles.None, CultureInfo.InvariantCulture, out int code))
            throw new IOException($"Respuesta no válida de la TV: {lines[0]}");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        int length = headers.TryGetValue("Content-Length", out string? value) && int.TryParse(value, out int parsed) ? parsed : 0;
        int total = end + HeaderEnd.Length + length;
        if (data.Length < total)
            return false;

        _received.RemoveRange(0, total);
        response = new AirPlayResponse(code, status.Length > 2 ? status[2] : "", headers, data[(end + HeaderEnd.Length)..total]);
        return true;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();
        _lock.Dispose();
    }
}
