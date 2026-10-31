"""Pruebas del rango de puertos fijos (ports.py): python -m unittest test_ports"""

import asyncio
import socket
import unittest

import ports


def _occupy(*numbers):
    socks = []
    for port in numbers:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.bind(("127.0.0.1", port))
        socks.append(sock)
    return socks


class BindUdpTest(unittest.TestCase):
    def bind(self):
        async def run():
            transport, _ = await ports.bind_udp(asyncio.DatagramProtocol, "127.0.0.1")
            port = transport.get_extra_info("sockname")[1]
            transport.close()
            return port
        return asyncio.run(run())

    def test_first_free_port(self):
        self.assertEqual(self.bind(), ports.PORTS[0])

    def test_skips_busy_ports(self):
        socks = _occupy(ports.PORTS[0], ports.PORTS[1])
        try:
            self.assertEqual(self.bind(), ports.PORTS[2])
        finally:
            for sock in socks:
                sock.close()

    def test_random_port_when_range_is_full(self):
        socks = _occupy(*ports.PORTS)
        try:
            self.assertNotIn(self.bind(), ports.PORTS)
        finally:
            for sock in socks:
                sock.close()


class FreeUdpPortsTest(unittest.TestCase):
    def test_distinct_ports_in_range(self):
        self.assertEqual(ports.free_udp_ports(2), [ports.PORTS[0], ports.PORTS[1]])

    def test_zero_when_range_is_full(self):
        socks = _occupy(*ports.PORTS[1:])
        try:
            self.assertEqual(ports.free_udp_ports(2), [ports.PORTS[0], 0])
        finally:
            for sock in socks:
                sock.close()


if __name__ == "__main__":
    unittest.main()
