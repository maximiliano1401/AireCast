// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AireCast.Core.Mirror;

namespace AireCast.Core.Audio;

/// <summary>
/// Envío del audio por UDP: al ritmo del reloj, en un hilo propio para que el vídeo no lo retrase, y
/// con un paquete de sincronía por segundo. Reenvía los paquetes que la TV pide por el canal de
/// control: sin eso, cada hueco suena a voz robótica.
/// </summary>
public sealed class AudioStream : IDisposable
{
    private const byte RetransmitRequest = 0x55;

    private readonly Socket _control;
    private readonly Socket _data;
    private readonly IPEndPoint? _remoteControl;
    private readonly IAudioPacketizer _packetizer;
    private readonly PcmBuffer _pcm;
    private readonly uint _latency;
    private readonly Dictionary<ushort, byte[]> _backlog = [];
    private readonly CancellationTokenSource _stop = new();
    private Thread? _sender;
    private Task? _receiver;
    private TimerResolution? _timer;
    private ushort _sequence = (ushort)Random.Shared.Next(ushort.MaxValue);

    public AudioStream(Socket control, IPEndPoint data, IPEndPoint? remoteControl, IAudioPacketizer packetizer, PcmBuffer pcm, int latency)
    {
        _control = control;
        _remoteControl = remoteControl;
        _packetizer = packetizer;
        _pcm = pcm;
        _latency = (uint)latency;
        _data = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _data.Connect(data);
    }

    public int ResentPackets { get; private set; }

    /// <summary>El margen con el que sale el audio: lo que la TV guarda antes de reproducirlo.</summary>
    public TimeSpan Latency => TimeSpan.FromSeconds((double)_latency / AudioFormat.Rate);

    public void Start()
    {
        _timer = TimerResolution.Begin(); // sin esto, el envío despierta cada ~15 ms y los paquetes salen de dos en dos
        _receiver = Task.Run(ReceiveControlAsync);
        _sender = new Thread(SendLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "AireCast-Audio" };
        _sender.Start();
    }

    private void SendLoop()
    {
        ulong startTime = AudioClock.SamplesFromNtp(NtpClock.Now());
        var clock = Stopwatch.StartNew();
        ulong sent = 0, nextSync = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                ulong due = (ulong)(clock.Elapsed.TotalSeconds * AudioFormat.Rate);
                while (sent + AudioFormat.SamplesPerPacket <= due)
                {
                    uint rtpTime = (uint)(sent + _latency);
                    bool first = sent == 0;
                    if (sent >= nextSync)
                    {
                        if (_remoteControl is not null)
                            _control.SendTo(_packetizer.Sync(startTime + sent, rtpTime, _latency, first), _remoteControl);
                        nextSync += AudioFormat.Rate;
                    }
                    Send(_packetizer.Packet(_pcm.TakePacket(), _sequence, rtpTime, first));
                    sent += AudioFormat.SamplesPerPacket;
                }
                Thread.Sleep(2);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // La sesión se cerró mientras se enviaba.
        }
    }

    private void Send(byte[] packet)
    {
        _data.Send(packet);
        lock (_backlog)
        {
            _backlog[_sequence] = packet;
            _backlog.Remove((ushort)(_sequence - AudioFormat.Backlog));
        }
        _sequence++;
    }

    private async Task ReceiveControlAsync()
    {
        byte[] buffer = new byte[64];
        var anywhere = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (true)
            {
                var received = await _control.ReceiveFromAsync(buffer, SocketFlags.None, anywhere, _stop.Token);
                if (received.ReceivedBytes == 8 && (buffer[1] & 0x7F) == RetransmitRequest)
                {
                    ushort first = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4));
                    int count = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(6));
                    Resend(first, count, received.RemoteEndPoint);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Terminó la sesión.
        }
    }

    private void Resend(ushort first, int count, EndPoint requester)
    {
        for (int i = 0; i < count; i++)
        {
            byte[]? packet;
            lock (_backlog)
                _backlog.TryGetValue((ushort)(first + i), out packet);
            if (packet is not null)
            {
                _control.SendTo(RetransmitPacket(packet), requester);
                ResentPackets++;
            }
        }
    }

    /// <summary>Reenvío: 0x80 0xD6, el número de secuencia original y el paquete tal cual.</summary>
    public static byte[] RetransmitPacket(byte[] packet) => [0x80, 0xD6, packet[2], packet[3], .. packet];

    public void Dispose()
    {
        _stop.Cancel();
        _sender?.Join(TimeSpan.FromSeconds(1));
        _timer?.Dispose();
        _data.Dispose();
        _control.Dispose();
        try
        {
            _receiver?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // El socket se cerró a medias: ya no importa.
        }
        _stop.Dispose();
    }
}
