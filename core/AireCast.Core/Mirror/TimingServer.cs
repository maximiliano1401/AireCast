// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AireCast.Core.Net;

namespace AireCast.Core.Mirror;

/// <summary>Responde a las consultas de hora de la TV: sin ellas no contesta al SETUP.</summary>
public sealed class TimingServer : IDisposable
{
    private const int PacketLength = 32;
    private const byte ReplyType = 0x53 | 0x80;
    private const ushort ReplySequence = 7;
    private const int SendTimeOffset = 24;

    private readonly Socket _socket;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public TimingServer(IPAddress address)
    {
        _socket = Ports.BindUdp(address);
        _loop = Task.Run(ServeAsync);
    }

    public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    public int Requests { get; private set; }

    /// <summary>La respuesta a una consulta: devuelve su hora de envío y da la nuestra como recepción y envío.</summary>
    public static byte[] BuildReply(ReadOnlySpan<byte> request, ulong now)
    {
        byte[] reply = new byte[PacketLength];
        reply[0] = request[0];
        reply[1] = ReplyType;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), ReplySequence);
        request.Slice(SendTimeOffset, 8).CopyTo(reply.AsSpan(8));
        BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(16), now);
        BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(24), now);
        return reply;
    }

    private async Task ServeAsync()
    {
        byte[] buffer = new byte[128];
        var anywhere = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (true)
            {
                var received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, anywhere, _stop.Token);
                if (received.ReceivedBytes < PacketLength)
                    continue;
                Requests++;
                byte[] reply = BuildReply(buffer, NtpClock.Now());
                await _socket.SendToAsync(reply, SocketFlags.None, received.RemoteEndPoint, _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // El socket pudo cerrarse a medias: ya no importa.
        }
        _socket.Dispose();
        _stop.Dispose();
    }
}
