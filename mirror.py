"""Duplicado de pantalla AirPlay 2 con emparejamiento HomeKit (sin FairPlay).

Replica el flujo que usa el emisor de PigeonCast (airplay_sender.dll, analizado con REA/Ghidra)
con receptores que soportan emparejamiento HomeKit (Roku, Samsung, LG…):

  1. pair-verify HomeKit -> canal RTSP cifrado (Control-Salt).
  2. SETUP de sesión: isScreenMirroringSession, timingProtocol NTP (+ canal de eventos).
  3. SETUP de stream type 110 con streamConnectionID -> la TV responde dataPort.
  4. RECORD de stream: le indica al decodificador de la TV que comience a procesar.
  5. TCP a dataPort: cabecera de 128 bytes + payload cifrado con ChaCha20-Poly1305.
"""

import asyncio
import logging
import random
import struct
import uuid

from pyatv.auth.hap_channel import setup_channel
from pyatv.auth.hap_pairing import parse_credentials
from pyatv.protocols.airplay.ap2_session import (
    DATASTREAM_INPUT_INFO,
    DATASTREAM_OUTPUT_INFO,
    DATASTREAM_SALT,
    EVENTS_READ_INFO,
    EVENTS_SALT,
    EVENTS_WRITE_INFO,
)
from pyatv.protocols.airplay.auth import verify_connection
from pyatv.protocols.airplay.channels import EventChannel
from pyatv.protocols.raop import timing
from pyatv.protocols.raop.protocols import TimingServer
from pyatv.support.chacha20 import Chacha20Cipher8byteNonce
from pyatv.support.http import decode_bplist_from_body, http_connect
from pyatv.support.rtsp import RtspSession

_LOGGER = logging.getLogger(__name__)

MIRROR_STREAM_TYPE = 110
PACKET_VIDEO = 0
PACKET_CODEC = 1
TIMESTAMP_NAMES = ["SubSu", "BePxT", "AfPxT", "BefEn", "EmEnc"]

SENDER_INFO = {
    "model": "iPad5,3",
    "osName": "iPhone OS",
    "osVersion": "15.8.2",
    "osBuildVersion": "19H384",
    "sourceVersion": "620.8.2",
}


def _header(size, ptype, option, ntp):
    """Cabecera de 128 bytes: tamaño, tipo, opción, marca NTP y relleno."""
    return struct.pack("<IBBBBQ", size, ptype, 0, option, 0, ntp)


def codec_packet(avcc, width, height):
    """Paquete de configuración del códec (avcC en claro)."""
    w, h = float(width), float(height)
    dims = struct.pack("<12f", w, h, 0.0, 0.0, 0.0, 0.0, w, h, 0.0, 0.0, w, h)
    header = _header(len(avcc), PACKET_CODEC, 0x16, timing.ntp_now()) + dims
    return header.ljust(128, b"\0") + avcc


def build_avcc(sps, pps):
    """Registro AVCDecoderConfigurationRecord a partir de SPS y PPS."""
    return (bytes([1, sps[1], sps[2], sps[3], 0xFF, 0xE1]) + struct.pack(">H", len(sps)) + sps
            + b"\x01" + struct.pack(">H", len(pps)) + pps)


def split_annexb(buf):
    """Divide un flujo Annex-B en NALs completos; devuelve (nals, resto)."""
    nals, start = [], None
    i, n = 0, len(buf)
    while i + 3 <= n:
        if buf[i] == 0 and buf[i + 1] == 0 and (buf[i + 2] == 1 or (i + 4 <= n and buf[i + 2] == 0 and buf[i + 3] == 1)):
            sc = 3 if buf[i + 2] == 1 else 4
            if start is not None:
                end = i
                while end > start and buf[end - 1] == 0:
                    end -= 1
                nals.append(bytes(buf[start:end]))
            start = i + sc
            i += sc
        else:
            i += 1
    if start is None:
        return nals, bytes(buf)
    return nals, b"\x00\x00\x00\x01" + bytes(buf[start:])


class MirrorSession:
    """Sesión de duplicado de pantalla hacia un receptor AirPlay 2."""

    def __init__(self, address, credentials, name="AireCast MVP", port=7000):
        self.address = address
        self.port = port
        self.credentials = parse_credentials(credentials)
        self.name = name
        self.connection = None
        self.rtsp = None
        self.verifier = None
        self.timing = None
        self.channels = []
        self.writer = None
        self.cipher = None
        self.stream_id = random.randint(1, 2**32 - 1)
        self.mac = ":".join(f"{random.randint(0, 255):02X}" for _ in range(6))
        self._keepalive = None

    async def start(self):
        loop = asyncio.get_running_loop()
        self.connection = await http_connect(self.address, self.port)
        self.verifier = await verify_connection(self.credentials, self.connection)
        self.rtsp = RtspSession(self.connection)

        _, self.timing = await loop.create_datagram_endpoint(
            TimingServer, local_addr=(self.connection.local_ip, 0))

        # 1. SETUP de sesión
        resp = await self._setup({
            "deviceID": self.mac,
            "macAddress": self.mac,
            "sessionUUID": str(uuid.uuid4()).upper(),
            "isScreenMirroringSession": True,
            "name": self.name,
            "statsCollectionEnabled": False,
            "timingPort": self.timing.port,
            "timingProtocol": "NTP",
            **SENDER_INFO,
        })
        _LOGGER.debug("SETUP de sesión: %s", resp)
        if "eventPort" in resp:
            transport, _ = await setup_channel(
                EventChannel, self.verifier, self.connection.remote_ip, resp["eventPort"],
                EVENTS_SALT, EVENTS_READ_INFO, EVENTS_WRITE_INFO)
            self.channels.append(transport)

        # 2. SETUP de flujo de vídeo (Stream 110)
        resp = await self._setup({"streams": [{
            "type": MIRROR_STREAM_TYPE,
            "streamConnectionID": self.stream_id,
            "timestampInfo": [{"name": n} for n in TIMESTAMP_NAMES],
        }]})
        _LOGGER.debug("SETUP de vídeo: %s", resp)
        data_port = resp["streams"][0]["dataPort"]

        # 3. Orden de RECORD requerida por Roku OS / AirPlay 2
        try:
            await self.rtsp.exchange("RECORD", body={"streams": [{"type": MIRROR_STREAM_TYPE}]}, allow_error=True)
        except Exception as exc:
            _LOGGER.debug("RECORD implícito o ignorado por el receptor: %s", exc)

        # 4. Derivación de claves y apertura de socket TCP
        out_key, in_key = self.verifier.encryption_keys(
            DATASTREAM_SALT + str(self.stream_id), DATASTREAM_OUTPUT_INFO, DATASTREAM_INPUT_INFO)
        
        self.cipher = Chacha20Cipher8byteNonce(out_key, in_key)
        _, self.writer = await asyncio.open_connection(self.connection.remote_ip, data_port)
        self._keepalive = asyncio.ensure_future(self._feedback_loop())
        return data_port

    async def _setup(self, body):
        resp = await self.rtsp.setup(body=body)
        return decode_bplist_from_body(resp) if resp.body else {}

    async def _feedback_loop(self):
        while True:
            await asyncio.sleep(2)
            try:
                await self.rtsp.feedback(allow_error=True)
            except Exception as exc:
                _LOGGER.debug("feedback falló: %s", exc)

    async def send_codec(self, sps, pps, width, height):
        self.writer.write(codec_packet(build_avcc(sps, pps), width, height))
        await self.writer.drain()

    async def send_frame(self, nals):
        """Envía un fotograma: NALs en formato AVCC, cifrados con la cabecera como AAD."""
        payload = b"".join(struct.pack(">I", len(n)) + n for n in nals)
        header = _header(len(payload) + 16, PACKET_VIDEO, 0, timing.ntp_now()).ljust(128, b"\0")
        encrypted_payload = self.cipher.encrypt(payload, aad=header)
        
        self.writer.write(header + encrypted_payload)
        await self.writer.drain()

    async def stop(self):
        if self._keepalive:
            self._keepalive.cancel()
        try:
            if self.rtsp:
                await self.rtsp.exchange("TEARDOWN", body={"streams": [{"type": MIRROR_STREAM_TYPE}]}, allow_error=True)
                await self.rtsp.exchange("TEARDOWN", allow_error=True)
        except Exception as exc:
            _LOGGER.debug("TEARDOWN falló: %s", exc)
        if self.writer:
            self.writer.close()
        for ch in self.channels:
            ch.close()
        if self.timing:
            self.timing.close()
        if self.connection:
            self.connection.close()


async def mirror_screen(session, ffmpeg, width=1280, height=720, fps=30, on_status=None):
    """Captura la pantalla con FFmpeg (H.264 Annex-B) y la envía por la sesión."""
    proc = await asyncio.create_subprocess_exec(
        ffmpeg, "-hide_banner", "-loglevel", "error",
        "-f", "gdigrab", "-framerate", str(fps), "-i", "desktop",
        "-vf", f"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2",
        "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
        "-profile:v", "baseline", "-level", "3.1", "-pix_fmt", "yuv420p",
        "-g", str(fps), "-keyint_min", str(fps),
        "-x264-params", "aud=1:repeat-headers=1",
        "-f", "h264", "-",
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
        creationflags=0x08000000,
    )
    sps = pps = None
    sent_codec = None
    frame = []
    buf = b""
    frames = 0
    try:
        while True:
            chunk = await proc.stdout.read(65536)
            if not chunk:
                err = (await proc.stderr.read()).decode(errors="replace").strip()
                raise RuntimeError(f"FFmpeg terminó: {err or 'sin salida'}")
            nals, buf = split_annexb(buf + chunk)
            for nal in nals:
                try:
                    ntype = nal[0] & 0x1F
                    if ntype == 9:  # AUD: marca el inicio de una nueva trama
                        if frame:
                            await session.send_frame(frame)
                            frames += 1
                            if on_status and frames % (fps * 2) == 0:
                                on_status(f"{frames} fotogramas enviados")
                        frame = []
                    elif ntype == 7:
                        sps = nal
                    elif ntype == 8:
                        pps = nal
                    else:
                        if sps and pps and sent_codec != (sps, pps):
                            await session.send_codec(sps, pps, width, height)
                            sent_codec = (sps, pps)
                        if sent_codec:
                            frame.append(nal)
                except Exception as e:
                    _LOGGER.error("Error al procesar/enviar trama NAL: %s", e)
                    raise
    except Exception as e:
        _LOGGER.error("Error en el bucle de captura mirror_screen: %s", e)
    finally:
        if proc.returncode is None:
            proc.kill()
            await proc.wait()