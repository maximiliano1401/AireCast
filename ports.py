"""Puertos locales fijos, para abrir solo esos en el cortafuegos (ver README, «Cortafuegos»).

La TV abre conexiones hacia el PC en:
  - UDP 27450-27459: consultas de hora (NTP) del duplicado y de pyatv (enviar vídeo y audio) y
    canal de control del audio.
  - TCP 27450-27459: el servidor HTTP de main.js que sirve los vídeos (MEDIA_PORTS, mismo rango).
  - UDP 5353: respuestas mDNS a la búsqueda (pyatv escucha ahí).

Cada socket toma el primer puerto libre del rango; si están todos ocupados, uno aleatorio, que
solo funcionará con el cortafuegos abierto. El rango queda por debajo de los puertos efímeros de
Linux (32768+) y Windows (49152+), así que el sistema no los da a otros programas por su cuenta.
"""

import asyncio
import logging
import socket

_LOGGER = logging.getLogger(__name__)

PORTS = range(27450, 27460)


async def bind_udp(protocol_factory, host):
    """create_datagram_endpoint en el primer puerto libre del rango. Devuelve (transport, protocol)."""
    loop = asyncio.get_running_loop()
    for port in PORTS:
        try:
            return await loop.create_datagram_endpoint(protocol_factory, local_addr=(host, port))
        except OSError as exc:  # ocupado, o reservado por el sistema (Hyper-V en Windows)
            _LOGGER.debug("Puerto UDP %d no disponible: %s", port, exc)
    _LOGGER.warning("Puertos UDP %d-%d ocupados: se usa uno aleatorio", PORTS[0], PORTS[-1])
    return await loop.create_datagram_endpoint(protocol_factory, local_addr=(host, 0))


def free_udp_ports(count):
    """Los primeros `count` puertos UDP libres del rango (0 = aleatorio si no quedan), para pyatv.

    Solo se comprueban y se sueltan: pyatv los vuelve a abrir enseguida.
    """
    found = []
    for port in PORTS:
        if len(found) == count:
            break
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
            try:
                sock.bind(("", port))
            except OSError:
                continue
        found.append(port)
    if len(found) < count:
        _LOGGER.warning("Puertos UDP %d-%d ocupados: pyatv usará aleatorios", PORTS[0], PORTS[-1])
    return found + [0] * (count - len(found))
