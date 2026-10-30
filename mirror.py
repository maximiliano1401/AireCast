"""Duplicado de pantalla AirPlay 2 con emparejamiento HomeKit (sin FairPlay).

Replica el flujo que usa el emisor de PigeonCast (airplay_sender.dll, analizado con REA/Ghidra)
con receptores que soportan emparejamiento HomeKit (Roku, Samsung, LG…):

  1. pair-verify HomeKit -> canal RTSP cifrado (Control-Salt).
  2. SETUP de sesión: isScreenMirroringSession, timingProtocol NTP (+ canal de eventos).
  3. SETUP de stream type 110 con streamConnectionID -> la TV responde dataPort.
  4. RECORD de stream: le indica al decodificador de la TV que comience a procesar.
  5. TCP a dataPort: cabecera de 128 bytes + payload cifrado con ChaCha20-Poly1305.

Audio (experimental): SETUP de stream type 96 (audio en tiempo real, como el RAOP de AirPlay 2
de pyatv) en la misma sesión. El audio del sistema se captura con WASAPI loopback (miniaudio)
y se envía por UDP como RTP con PCM 16 bits, cifrado con ChaCha20 usando la clave `shk` que
nosotros mismos enviamos en el SETUP. Paquetes de sync por el controlPort ligan RTP <-> NTP.

Apple TV antiguos con emparejamiento legacy (AppleTV3,2), siguiendo el flujo de doubletake:

  1. pair-verify legacy (sin cifrado del canal RTSP) + FairPlay SAP en POST /fp-setup.
  2. SETUP "media-first": sesión + flujo de audio (sin paquetes) + clave AES envuelta (ekey/eiv).
  3. SETUP de vídeo con la clave en shk/shiv -> dataPort; RECORD.
  4. Vídeo cifrado con AES-128-CTR (clave e IV derivados con SHA-512 de shk + streamConnectionID).
Si la TV responde 401 (modo "código en pantalla"), muestra un código nuevo en cada conexión: se le
pide al usuario y se reintenta con Digest (usuario "AirPlay", contraseña = ese código).
"""

import array
import asyncio
import hashlib
import logging
import os
import random
import re
import struct
import uuid

import miniaudio
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
from pyatv.auth.hap_channel import setup_channel
from pyatv.auth.hap_pairing import AuthenticationType, parse_credentials
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
from pyatv.protocols.raop.packets import AudioPacketHeader, SyncPacket
from pyatv.protocols.raop.protocols import TimingServer
from pyatv.support.chacha20 import Chacha20Cipher8byteNonce
from pyatv.support.http import decode_bplist_from_body, http_connect
from pyatv.support.rtsp import DigestInfo, RtspSession, get_digest_payload

from fairplay import FairPlaySession

_LOGGER = logging.getLogger(__name__)

MIRROR_STREAM_TYPE = 110
PACKET_VIDEO = 0
PACKET_CODEC = 1
TIMESTAMP_NAMES = ["SubSu", "BePxT", "AfPxT", "BefEn", "EmEnc"]

AUDIO_STREAM_TYPE = 96
AUDIO_RATE = 44100
AUDIO_CHANNELS = 2
AUDIO_SPF = 352  # muestras por paquete
AUDIO_PACKET_BYTES = AUDIO_SPF * AUDIO_CHANNELS * 2
AUDIO_LATENCY = AUDIO_RATE // 40  # 25 ms: si va atrasado, bajar; si va adelantado o se corta, subir
AUDIO_MAX_BUFFER = AUDIO_RATE // 25 * AUDIO_CHANNELS * 2  # descarta audio con >40 ms de retraso

SENDER_INFO = {
    "model": "iPad5,3",
    "osName": "iPhone OS",
    "osVersion": "15.8.2",
    "osBuildVersion": "19H384",
    "sourceVersion": "620.8.2",
}

# Identidad que doubletake presenta a los receptores legacy (probada con AppleTV3,2).
LEGACY_SENDER_INFO = {
    "model": "Linux",
    "osBuildVersion": "13F69",
    "sourceVersion": "280.33",
}
LEGACY_VIDEO_LATENCY_MS = 75
FRAME_IDR = 0x10
MAX_CODE_ATTEMPTS = 3


def _header(size, ptype, option, ntp, flags=0):
    """Cabecera de 128 bytes: tamaño, tipo, flags (0x10 = IDR), opción, marca NTP y relleno."""
    return struct.pack("<IBBBBQ", size, ptype, flags, option, 0, ntp)


def derive_video_keys(shk, stream_id):
    """Clave e IV de AES-CTR para el vídeo legacy: SHA-512("AirPlayStream{Key,IV}<id>" + shk)[:16]."""
    key = hashlib.sha512(f"AirPlayStreamKey{stream_id}".encode() + shk).digest()[:16]
    iv = hashlib.sha512(f"AirPlayStreamIV{stream_id}".encode() + shk).digest()[:16]
    return key, iv


class AesCtrFrameCipher:
    """AES-128-CTR por fotograma, con la alineación a bloques que espera el receptor.

    Cada fotograma empieza en un bloque CTR nuevo; si el anterior terminó a medio bloque, los
    bytes de keystream sobrantes se usan primero para el inicio del siguiente fotograma.
    """

    def __init__(self, key, iv):
        self._ctr = Cipher(algorithms.AES(key), modes.CTR(iv)).encryptor()
        self._leftover = b""

    def encrypt(self, payload):
        out = bytearray(payload)
        n = min(len(self._leftover), len(out))
        for i in range(n):
            out[i] ^= self._leftover[i]
        self._leftover = self._leftover[n:]
        if self._leftover:
            return bytes(out)
        full = (len(out) - n) // 16 * 16
        out[n:n + full] = self._ctr.update(bytes(out[n:n + full]))
        rest = len(out) - n - full
        if rest:
            keystream = self._ctr.update(bytes(16))
            for i in range(rest):
                out[n + full + i] ^= keystream[i]
            self._leftover = keystream[rest:]
        return bytes(out)


class _PlainEventChannel(asyncio.Protocol):
    """Canal de eventos sin cifrar (receptores legacy): responde 200 OK a cada petición RTSP."""

    def connection_made(self, transport):
        self.transport = transport
        self.buffer = b""

    def data_received(self, data):
        self.buffer += data
        while b"\r\n\r\n" in self.buffer:
            head, rest = self.buffer.split(b"\r\n\r\n", 1)
            lines = head.split(b"\r\n")
            headers = {k.strip().lower(): v.strip() for k, _, v in (l.partition(b":") for l in lines[1:])}
            length = int(headers.get(b"content-length", 0))
            if len(rest) < length:
                break
            self.buffer = rest[length:]
            _LOGGER.debug("Canal de eventos: %s", lines[0])
            protocol = lines[0].rsplit(b" ", 1)[-1]
            cseq = b"CSeq: " + headers[b"cseq"] + b"\r\n" if b"cseq" in headers else b""
            self.transport.write(protocol + b" 200 OK\r\n" + cseq + b"Content-Length: 0\r\nAudio-Latency: 0\r\n\r\n")


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


class LoopbackDevice(miniaudio.CaptureDevice):
    """Captura lo que suena en el dispositivo de salida predeterminado (WASAPI loopback).

    miniaudio.CaptureDevice solo abre micrófonos; esto replica su __init__ con el tipo loopback.
    """

    def __init__(self, sample_rate=AUDIO_RATE, nchannels=AUDIO_CHANNELS, buffersize_msec=10):
        miniaudio.AbstractDevice.__init__(self)
        ffi, lib = miniaudio.ffi, miniaudio.lib
        self.format = miniaudio.SampleFormat.SIGNED16
        self.sample_width = 2
        self.nchannels = nchannels
        self.sample_rate = sample_rate
        self.buffersize_msec = buffersize_msec
        self._ffi_handle = ffi.new_handle(self)
        self._devconfig = lib.ma_device_config_init(lib.ma_device_type_loopback)
        self._devconfig.sampleRate = sample_rate
        self._devconfig.capture.channels = nchannels
        self._devconfig.capture.format = self.format.value
        self._devconfig.capture.pDeviceID = ffi.NULL
        self._devconfig.periodSizeInMilliseconds = buffersize_msec
        self._devconfig.pUserData = self._ffi_handle
        self._devconfig.dataCallback = lib._internal_data_callback
        self._devconfig.stopCallback = lib._internal_stop_callback
        self.callback_generator = None
        self._context = self._make_context([miniaudio.Backend.WASAPI])
        result = lib.ma_device_init(self._context, ffi.addressof(self._devconfig), self._device)
        if result != lib.MA_SUCCESS:
            raise miniaudio.MiniaudioError("no se pudo abrir la captura loopback", result)
        self.backend = "wasapi"


class _ControlProtocol(asyncio.DatagramProtocol):
    """Socket del controlPort: envía sync e ignora las peticiones de retransmisión."""

    def datagram_received(self, data, addr):
        _LOGGER.debug("controlPort recibió %d bytes (retransmisión no soportada)", len(data))


class AudioStream:
    """Flujo de audio en tiempo real (type 96) dentro de una sesión de duplicado."""

    def __init__(self, session):
        self.session = session
        self.key = os.urandom(32)
        self.ssrc = random.randint(1, 2**32 - 1)
        self.cipher = Chacha20Cipher8byteNonce(self.key, self.key)
        self.control = None
        self.data = None
        self.data_port = None
        self.control_port = None
        self.device = None
        self._pcm = bytearray()
        self._task = None
        self._seq = random.randint(0, 2**16 - 1)

    async def setup(self):
        loop = asyncio.get_running_loop()
        local_ip = self.session.connection.local_ip
        self.control, _ = await loop.create_datagram_endpoint(
            _ControlProtocol, local_addr=(local_ip, 0))
        resp = await self.session._setup({"streams": [{
            "type": AUDIO_STREAM_TYPE,
            "ct": 1,  # PCM sin comprimir (16 bits big-endian)
            "audioFormat": 0x800,  # PCM 44100/16/2
            "audioMode": "default",
            "spf": AUDIO_SPF,
            "sr": AUDIO_RATE,
            "shk": self.key,
            "controlPort": self.control.get_extra_info("sockname")[1],
            "isMedia": True,
            "usingScreen": True,
            "latencyMin": AUDIO_LATENCY,
            "latencyMax": AUDIO_LATENCY * 4,
            "supportsDynamicStreamID": False,
            "streamConnectionID": self.ssrc,
        }]})
        _LOGGER.debug("SETUP de audio: %s", resp)
        stream = resp["streams"][0]
        self.data_port = stream["dataPort"]
        self.control_port = stream.get("controlPort")
        self.data, _ = await loop.create_datagram_endpoint(
            asyncio.DatagramProtocol, remote_addr=(self.session.connection.remote_ip, self.data_port))

    def start(self):
        loop = asyncio.get_running_loop()

        def capture():
            while True:
                data = yield
                loop.call_soon_threadsafe(self._pcm.extend, data)

        gen = capture()
        next(gen)
        self.device = LoopbackDevice()
        self.device.start(gen)
        self._task = asyncio.ensure_future(self._send_loop())

    def _take(self):
        """Saca un paquete de PCM del búfer; rellena con silencio (WASAPI no entrega nada si no suena nada)."""
        if len(self._pcm) > AUDIO_MAX_BUFFER:
            del self._pcm[:len(self._pcm) - AUDIO_MAX_BUFFER]
        chunk = bytes(self._pcm[:AUDIO_PACKET_BYTES])
        del self._pcm[:AUDIO_PACKET_BYTES]
        samples = array.array("h", chunk.ljust(AUDIO_PACKET_BYTES, b"\0"))
        samples.byteswap()  # miniaudio entrega little-endian; AirPlay espera big-endian
        return samples.tobytes()

    async def _send_loop(self):
        """Envía paquetes al ritmo del reloj, con la misma relación RTP <-> NTP que pyatv."""
        loop = asyncio.get_running_loop()
        start_ts = timing.ntp2ts(timing.ntp_now(), AUDIO_RATE)
        started = loop.time()
        sent = 0
        next_sync = 0
        try:
            while True:
                due = int((loop.time() - started) * AUDIO_RATE)
                while sent + AUDIO_SPF <= due:
                    rtptime = sent + AUDIO_LATENCY
                    if sent >= next_sync:
                        self._send_sync(start_ts + sent, rtptime, first=sent == 0)
                        next_sync += AUDIO_RATE
                    self._send_packet(self._take(), rtptime, first=sent == 0)
                    sent += AUDIO_SPF
                await asyncio.sleep(AUDIO_SPF / AUDIO_RATE)
        except asyncio.CancelledError:
            pass
        except Exception as exc:
            _LOGGER.error("Error al enviar audio: %s", exc)

    def _send_sync(self, head_ts, rtptime, first):
        if not self.control_port:
            return
        sec, frac = timing.ntp2parts(timing.ts2ntp(head_ts, AUDIO_RATE))
        packet = SyncPacket.encode(0x90 if first else 0x80, 0xD4, 0x0007,
                                   rtptime - AUDIO_LATENCY, sec, frac, rtptime)
        self.control.sendto(packet, (self.session.connection.remote_ip, self.control_port))

    def _send_packet(self, pcm, rtptime, first):
        header = AudioPacketHeader.encode(0x80, 0xE0 if first else 0x60, self._seq,
                                          rtptime & 0xFFFFFFFF, self.ssrc)
        # El nonce que usará este encrypt va al final del paquete (8 bytes), como en pyatv.
        nonce = self.cipher.out_nonce
        payload = self.cipher.encrypt(pcm, aad=header[4:12])
        self.data.sendto(header + payload + nonce[-8:])
        self._seq = (self._seq + 1) % 2**16

    def stop(self):
        if self._task:
            self._task.cancel()
        if self.device:
            self.device.close()
        for transport in (self.data, self.control):
            if transport:
                transport.close()


class MirrorSession:
    """Sesión de duplicado de pantalla hacia un receptor AirPlay 2."""

    def __init__(self, address, credentials, name="AireCast MVP", port=7000, audio=True, ask_code=None):
        self.address = address
        self.want_audio = audio
        self.audio = None
        self.port = port
        self.credentials = parse_credentials(credentials)
        self.ask_code = ask_code  # async (retry: bool) -> código que muestra la TV
        self.name = name
        self.connection = None
        self.rtsp = None
        self.verifier = None
        self.timing = None
        self.control = None
        self.channels = []
        self.writer = None
        self.cipher = None
        self.frame_cipher = None
        self.stream_id = random.randint(1, 2**32 - 1)
        self.session_uuid = str(uuid.uuid4()).upper()
        self.mac = ":".join(f"{random.randint(0, 255):02X}" for _ in range(6))
        self._keepalive = None

    @property
    def legacy(self):
        """Emparejamiento legacy (Apple TV antiguos): sin HomeKit, requiere FairPlay."""
        return self.credentials.type == AuthenticationType.Legacy

    async def start(self):
        loop = asyncio.get_running_loop()
        self.connection = await http_connect(self.address, self.port)
        self.verifier = await verify_connection(self.credentials, self.connection)
        self.rtsp = RtspSession(self.connection)

        _, self.timing = await loop.create_datagram_endpoint(
            TimingServer, local_addr=(self.connection.local_ip, 0))

        if self.legacy:
            return await self._start_legacy(loop)

        # 1. SETUP de sesión
        resp = await self._setup({
            "deviceID": self.mac,
            "macAddress": self.mac,
            "sessionUUID": self.session_uuid,
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

        # 2b. SETUP de flujo de audio (Stream 96). Si la TV lo rechaza, seguimos solo con vídeo.
        if self.want_audio:
            audio = AudioStream(self)
            try:
                await audio.setup()
                self.audio = audio
            except Exception as exc:
                _LOGGER.warning("La TV no aceptó el flujo de audio: %s", exc)
                audio.stop()

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
        if self.audio:
            try:
                self.audio.start()
            except Exception as exc:
                _LOGGER.warning("No se pudo capturar el audio del sistema: %s", exc)
                self.audio.stop()
                self.audio = None
        return data_port

    async def _start_legacy(self, loop):
        """Apple TV con AirPlay 1 (AppleTV3,2): FairPlay + SETUP media-first + vídeo AES-CTR."""
        # 1. FairPlay SAP: m1 -> m2, m3 -> m4. Después la clave AES del vídeo viaja envuelta (ekey).
        fairplay = FairPlaySession()
        m2 = await self._fp_setup(fairplay.message1())
        fairplay.confirm(await self._fp_setup(fairplay.message3(m2)))
        key, iv = os.urandom(16), os.urandom(16)

        # 2. SETUP media-first: crea la sesión con un flujo de audio que el receptor exige,
        #    aunque no le mandemos paquetes (solo vídeo por ahora).
        self.control, _ = await loop.create_datagram_endpoint(
            _ControlProtocol, local_addr=(self.connection.local_ip, 0))
        resp = await self._setup({
            "deviceID": self.mac,
            "macAddress": self.mac,
            "sessionUUID": self.session_uuid,
            "isScreenMirroringSession": True,
            "name": self.name,
            "timingPort": self.timing.port,
            "timingProtocol": "NTP",
            **LEGACY_SENDER_INFO,
            "ekey": fairplay.wrap_key(key),
            "eiv": iv,
            "et": 32,
            "streams": [{
                "type": AUDIO_STREAM_TYPE,
                "streamConnectionID": random.randint(1, 2**63 - 1),
                "ct": 2,  # ALAC (descriptor de relleno: no se envía audio)
                "spf": AUDIO_SPF,
                "sr": AUDIO_RATE,
                "audioFormat": 0x40000,
                "audioMode": "default",
                "usingScreen": True,
                "latencyMin": 0,
                "latencyMax": AUDIO_LATENCY,
                "controlPort": self.control.get_extra_info("sockname")[1],
            }],
        })
        _LOGGER.debug("SETUP de sesión (legacy): %s", resp)
        if resp.get("eventPort"):
            try:
                transport, _ = await loop.create_connection(
                    _PlainEventChannel, self.connection.remote_ip, resp["eventPort"])
                self.channels.append(transport)
            except OSError as exc:
                _LOGGER.warning("No se pudo abrir el canal de eventos: %s", exc)

        # 3. SETUP de vídeo con la clave en claro (shk/shiv) -> dataPort
        resp = await self._setup({"streams": [{
            "type": MIRROR_STREAM_TYPE,
            "streamConnectionID": self.stream_id,
            "latencyMs": LEGACY_VIDEO_LATENCY_MS,
            "timestampInfo": [{"name": n} for n in TIMESTAMP_NAMES],
            "shk": key,
            "shiv": iv,
        }]})
        _LOGGER.debug("SETUP de vídeo (legacy): %s", resp)
        data_port = resp["streams"][0]["dataPort"]

        # 4. Socket de datos, RECORD y cifrado AES-CTR
        _, self.writer = await asyncio.open_connection(self.connection.remote_ip, data_port)
        resp = await self._authorized(lambda: self.rtsp.exchange("RECORD", headers={
            "Session": self.session_uuid, "Range": "npt=0-", "RTP-Info": "seq=0;rtptime=0"}, allow_error=True))
        if resp.code >= 400:
            raise RuntimeError(f"La TV rechazó el RECORD: {resp.code} {resp.message}")
        self.frame_cipher = AesCtrFrameCipher(*derive_video_keys(key, self.stream_id))
        self._keepalive = asyncio.ensure_future(self._feedback_loop())
        return data_port

    async def _authorized(self, send):
        """Hace la petición y, si la TV responde 401, pide el código que muestra y reintenta con Digest."""
        for attempt in range(MAX_CODE_ATTEMPTS + 1):
            resp = await send()
            if resp.code != 401:
                return resp
            if attempt == MAX_CODE_ATTEMPTS:
                raise RuntimeError("Código de AirPlay incorrecto")
            if not self.ask_code:
                raise RuntimeError("La TV pide el código de AirPlay que muestra en pantalla")
            challenge = dict(re.findall(r'(\w+)="([^"]*)"', resp.headers.get("WWW-Authenticate", "")))
            _LOGGER.debug("La TV pide Digest (realm=%s), pidiendo el código al usuario", challenge.get("realm"))
            code = await self.ask_code(attempt > 0)
            self.rtsp.digest_info = DigestInfo(
                "AirPlay", challenge.get("realm", "airplay"), code, challenge.get("nonce", ""))

    async def _fp_setup(self, message):
        def send():
            headers = {"Content-Type": "application/octet-stream", "X-Apple-ET": "32"}
            if self.rtsp.digest_info:
                headers["Authorization"] = get_digest_payload("POST", "/fp-setup", *self.rtsp.digest_info)
            return self.connection.post("/fp-setup", headers=headers, body=message, allow_error=True)

        resp = await self._authorized(send)
        if resp.code != 200:
            raise RuntimeError(f"La TV rechazó FairPlay (/fp-setup): {resp.code} {resp.message}")
        return bytes(resp.body)

    async def _setup(self, body):
        resp = await self._authorized(lambda: self.rtsp.exchange("SETUP", body=body, allow_error=True))
        if resp.code >= 400:
            raise RuntimeError(f"La TV rechazó el SETUP: {resp.code} {resp.message}")
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
        if self.frame_cipher:  # legacy: AES-CTR, sin etiqueta de autenticación
            flags = FRAME_IDR if any(n[0] & 0x1F == 5 for n in nals) else 0
            header = _header(len(payload), PACKET_VIDEO, 0, timing.ntp_now(), flags).ljust(128, b"\0")
            self.writer.write(header + self.frame_cipher.encrypt(payload))
            await self.writer.drain()
            return
        header = _header(len(payload) + 16, PACKET_VIDEO, 0, timing.ntp_now()).ljust(128, b"\0")
        encrypted_payload = self.cipher.encrypt(payload, aad=header)
        
        self.writer.write(header + encrypted_payload)
        await self.writer.drain()

    async def stop(self):
        if self._keepalive:
            self._keepalive.cancel()
        if self.audio:
            self.audio.stop()
        try:
            if self.rtsp:
                if self.audio:
                    await self.rtsp.exchange("TEARDOWN", body={"streams": [{"type": AUDIO_STREAM_TYPE}]}, allow_error=True)
                await self.rtsp.exchange("TEARDOWN", body={"streams": [{"type": MIRROR_STREAM_TYPE}]}, allow_error=True)
                await self.rtsp.exchange("TEARDOWN", allow_error=True)
        except Exception as exc:
            _LOGGER.debug("TEARDOWN falló: %s", exc)
        if self.writer:
            self.writer.close()
        for ch in self.channels:
            ch.close()
        if self.control:
            self.control.close()
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