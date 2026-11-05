// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AireCast.Core.Discovery;

/// <summary>
/// Busca receptores AirPlay por mDNS y repregunta cada segundo por lo que aún falte de cada
/// servicio (SRV, TXT, dirección). Pregunta desde dos sockets, porque cada cortafuegos deja pasar
/// una respuesta distinta: desde el 5353 las respuestas llegan por multicast a ese puerto, y desde
/// un puerto alto llegan por unicast a ese puerto (la zona FedoraWorkstation bloquea el 5353).
/// </summary>
public static class AirPlayBrowser
{
    private const string AirPlay = "_airplay._tcp.local";
    private const string Raop = "_raop._tcp.local";
    private const string Dmap = "_touch-able._tcp.local";
    private const int MdnsPort = 5353;
    private static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");
    private static readonly TimeSpan QueryInterval = TimeSpan.FromSeconds(1);

    public static async Task<IReadOnlyList<AirPlayDevice>> DiscoverAsync(TimeSpan duration, CancellationToken ct = default)
    {
        IPAddress[] interfaces = LocalAddresses();
        using var mdns = OpenMdnsSocket(interfaces);
        using var unicast = OpenSocket(0);
        Socket[] sockets = [mdns, unicast];
        var records = new RecordSet();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(duration);
        Task receiving = Task.WhenAll(sockets.Select(socket => ReceiveAsync(socket, records, timeout.Token)));
        var group = new IPEndPoint(MdnsGroup, MdnsPort);
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                byte[] query = DnsMessage.BuildQuery(records.PendingQuestions([AirPlay, Raop, Dmap]));
                foreach (Socket socket in sockets)
                {
                    foreach (IPAddress address in interfaces)
                    {
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                        try
                        {
                            await socket.SendToAsync(query, SocketFlags.None, group, timeout.Token);
                        }
                        catch (SocketException)
                        {
                            // Una interfaz sin red (un puente de Docker apagado) no impide usar las demás.
                        }
                    }
                }
                await Task.Delay(QueryInterval, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Terminó el tiempo de búsqueda.
        }
        await receiving;
        ct.ThrowIfCancellationRequested();
        return records.Devices(AirPlay, Raop, Dmap);
    }

    private static IPAddress[] LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(i => i.OperationalStatus == OperationalStatus.Up
                && i.NetworkInterfaceType != NetworkInterfaceType.Loopback && i.SupportsMulticast)
            .SelectMany(i => i.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .ToArray();

    private static Socket OpenMdnsSocket(IPAddress[] interfaces)
    {
        Socket socket = OpenSocket(MdnsPort);
        foreach (IPAddress address in interfaces)
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MdnsGroup, address));
        return socket;
    }

    private static Socket OpenSocket(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ExclusiveAddressUse = false;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        return socket;
    }

    private static async Task ReceiveAsync(Socket socket, RecordSet records, CancellationToken ct)
    {
        byte[] buffer = new byte[9000];
        var anywhere = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (true)
            {
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, anywhere, ct);
                try
                {
                    records.Add(DnsMessage.Parse(buffer.AsSpan(0, received.ReceivedBytes)));
                }
                catch (FormatException)
                {
                    // Un paquete raro de otro equipo de la red no debe detener la búsqueda.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
