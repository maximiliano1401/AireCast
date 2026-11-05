// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;

namespace AireCast.Core.Net;

/// <summary>
/// Puertos locales fijos, para abrir solo esos en el cortafuegos (ver «Cortafuegos» en el README).
/// Cada socket toma el primero libre del rango; si están todos ocupados, uno aleatorio.
/// </summary>
public static class Ports
{
    public const int First = 27450;
    public const int Last = 27459;

    public static Socket BindUdp(IPAddress address)
    {
        for (int port = First; port <= Last; port++)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.Bind(new IPEndPoint(address, port));
                return socket;
            }
            catch (SocketException)
            {
                socket.Dispose(); // ocupado, o reservado por el sistema (Hyper-V en Windows)
            }
        }
        var random = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        random.Bind(new IPEndPoint(address, 0));
        return random;
    }
}
