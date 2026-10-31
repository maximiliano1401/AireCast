"""Puente AirPlay para AireCast MVP.

Habla JSON por líneas: lee comandos en stdin ({"id", "cmd", ...}) y responde en stdout
({"id", "ok", "result"|"error"}) o emite eventos ({"event", ...}).
Las credenciales de emparejamiento se guardan en el fichero que se pasa como argumento.
"""

import asyncio
import json
import logging
import sys
from contextlib import asynccontextmanager
from pathlib import Path

import pyatv
from pyatv.const import Protocol
from pyatv.exceptions import HttpError
from pyatv.protocols.airplay import player
from pyatv.protocols.airplay.player import AirPlayPlayer
from pyatv.protocols.raop.protocols import TimingServer
from pyatv.storage.memory_storage import MemoryStorage
from pyatv.support.rtsp import RtspSession

from mirror import MirrorSession, mirror_screen, plat, screen_capture
from ports import bind_udp, free_udp_ports

# Tras POST /play, pyatv envía PUT /setProperty y POST /rate pensados para Apple TV.
# Las TVs con AirPlay de terceros (Roku, Samsung, LG…) responden 501 Not Implemented
# y pyatv aborta la reproducción. Son opcionales: toleramos el error en esas rutas.
_OPTIONAL_URIS = ("/setProperty", "/rate")
_exchange = RtspSession.exchange


async def _tolerant_exchange(self, method, uri=None, *args, **kwargs):
    if uri and uri.startswith(_OPTIONAL_URIS):
        kwargs["allow_error"] = True
    return await _exchange(self, method, uri, *args, **kwargs)


RtspSession.exchange = _tolerant_exchange

# pyatv sigue la reproducción consultando GET /playback-info. Si la TV no lo implementa,
# no sabemos cuándo acaba: mantenemos la sesión abierta (cerrarla corta el vídeo) hasta
# que el usuario pulse Detener.
_wait_for_end = AirPlayPlayer._wait_for_media_to_end


async def _tolerant_wait_for_end(self):
    try:
        await _wait_for_end(self)
    except HttpError as exc:
        emit({"event": "info", "message": f"La TV no informa del progreso ({exc}); pulsa Detener para terminar."})
        await asyncio.Event().wait()


AirPlayPlayer._wait_for_media_to_end = _tolerant_wait_for_end


# Al enviar un vídeo, la TV consulta la hora a un servidor NTP que pyatv abre en un puerto
# aleatorio: lo abrimos en el rango fijo de ports.py, para el cortafuegos.
@asynccontextmanager
async def _fixed_port_timing_server(rtsp):
    _, server = await bind_udp(TimingServer, rtsp.connection.local_ip)
    try:
        yield server
    finally:
        server.close()


player.timing_server = _fixed_port_timing_server

CREDS_FILE = Path(sys.argv[1] if len(sys.argv) > 1 else "credentials.json")

# Registro detallado del protocolo, junto a las credenciales, para diagnosticar fallos.
CREDS_FILE.parent.mkdir(parents=True, exist_ok=True)
logging.basicConfig(
    filename=CREDS_FILE.parent / "airplay-debug.log", filemode="w", level=logging.DEBUG,
    format="%(asctime)s %(name)s %(message)s",
)


def load_creds():
    try:
        return json.loads(CREDS_FILE.read_text("utf-8"))
    except (OSError, ValueError):
        return {}


def save_creds(creds):
    CREDS_FILE.parent.mkdir(parents=True, exist_ok=True)
    CREDS_FILE.write_text(json.dumps(creds, indent=2), "utf-8")


def emit(obj):
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


class Bridge:
    def __init__(self, loop):
        self.loop = loop
        self.configs = {}  # identifier -> config
        self.pairing = None
        self.atv = None
        self.task = None  # reproducción en curso
        self.code_waiter = None  # código de AirPlay que la interfaz debe pedir al usuario

    async def ask_code(self, retry):
        """Pide a la interfaz el código que muestra la TV (Apple TV en modo "código en pantalla")."""
        self.code_waiter = self.loop.create_future()
        emit({"event": "code-required", "retry": retry})
        try:
            return await asyncio.wait_for(self.code_waiter, timeout=120)
        except asyncio.TimeoutError:
            raise RuntimeError("No se escribió el código de la TV a tiempo") from None
        finally:
            self.code_waiter = None

    async def mirror_code(self, msg):
        if not self.code_waiter or self.code_waiter.done():
            raise RuntimeError("La TV no está esperando ningún código")
        self.code_waiter.set_result(str(msg["code"]).strip())
        return {"sent": True}

    async def find(self, device_id):
        conf = self.configs.get(device_id)
        if conf is None:
            found = await pyatv.scan(self.loop, identifier=device_id, timeout=5)
            if not found:
                raise RuntimeError("Dispositivo no encontrado")
            conf = self.configs[device_id] = found[0]
        return conf

    async def scan(self, msg):
        confs = await pyatv.scan(self.loop, timeout=msg.get("timeout", 5))
        creds = load_creds()
        devices = []
        for conf in confs:
            airplay = conf.get_service(Protocol.AirPlay)
            raop = conf.get_service(Protocol.RAOP)
            if not airplay and not raop:
                continue
            self.configs[conf.identifier] = conf
            devices.append({
                "id": conf.identifier,
                "name": conf.name,
                "address": str(conf.address),
                "model": conf.device_info.model_str,
                "video": bool(airplay) and conf.device_info.model_str != "HomePod",
                "audio": bool(raop),
                "needs_pairing": bool(airplay) and airplay.pairing.name == "Mandatory",
                "paired": conf.identifier in creds,
            })
        return devices

    async def pair_begin(self, msg):
        if self.pairing:
            await self.pairing.close()
        conf = await self.find(msg["device"])
        self.pairing = await pyatv.pair(conf, Protocol.AirPlay, self.loop)
        await self.pairing.begin()
        return {"device_provides_pin": self.pairing.device_provides_pin}

    async def pair_pin(self, msg):
        if not self.pairing:
            raise RuntimeError("No hay emparejamiento en curso")
        try:
            self.pairing.pin(int(msg["pin"]))
            await self.pairing.finish()
            if not self.pairing.has_paired:
                raise RuntimeError("PIN incorrecto")
            creds = load_creds()
            creds[msg["device"]] = self.pairing.service.credentials
            save_creds(creds)
            return {"paired": True}
        finally:
            await self.pairing.close()
            self.pairing = None

    async def connect(self, device_id):
        await self.disconnect()
        conf = await self.find(device_id)
        cred = load_creds().get(device_id)
        if cred:
            conf.set_credentials(Protocol.AirPlay, cred)
        # Puertos de hora y control de RAOP (enviar audio) en el rango fijo de ports.py.
        storage = MemoryStorage()
        raop = (await storage.get_settings(conf)).protocols.raop
        raop.timing_port, raop.control_port = free_udp_ports(2)
        self.atv = await pyatv.connect(conf, self.loop, storage=storage)
        return self.atv

    async def disconnect(self):
        if self.task and not self.task.done():
            self.task.cancel()
            try:
                await self.task
            except BaseException:
                pass
        self.task = None
        if self.atv:
            self.atv.close()
            self.atv = None

    def run_playback(self, coro, kind):
        async def runner():
            try:
                await coro
                emit({"event": "ended", "kind": kind})
            except asyncio.CancelledError:
                emit({"event": "stopped", "kind": kind})
            except Exception as exc:  # noqa: BLE001 - se informa a la interfaz
                emit({"event": "error", "kind": kind, "error": str(exc) or type(exc).__name__})

        self.task = asyncio.ensure_future(runner())

    async def play_url(self, msg):
        atv = await self.connect(msg["device"])
        self.run_playback(atv.stream.play_url(msg["url"]), "video")
        return {"playing": True}

    async def stream_audio(self, msg):
        atv = await self.connect(msg["device"])
        self.run_playback(atv.stream.stream_file(msg["path"]), "audio")
        return {"playing": True}

    async def mirror(self, msg):
        """Duplica la pantalla (AirPlay 2 + HomeKit, como PigeonCast). Ver mirror.py."""
        await self.disconnect()
        conf = await self.find(msg["device"])
        cred = load_creds().get(msg["device"])
        if not cred:
            raise RuntimeError("Empareja primero la TV (hace falta el PIN)")
        # Primero la captura: en Wayland el escritorio puede pedir permiso, y la TV no debe esperar.
        capture = screen_capture(CREDS_FILE.parent)
        await capture.open()
        session = MirrorSession(str(conf.address), cred, port=conf.get_service(Protocol.AirPlay).port,
                                ask_code=self.ask_code)
        try:
            await session.start()
        except BaseException:
            await session.stop()
            await capture.close()
            raise
        emit({"event": "info", "message": "Audio: activo" if session.audio else
              "Audio: no disponible, solo vídeo (detalles en airplay-debug.log)"})

        async def run():
            try:
                await mirror_screen(session, msg["ffmpeg"], capture,
                                    on_status=lambda t: emit({"event": "info", "message": t}))
            finally:
                await session.stop()
                await capture.close()

        self.run_playback(run(), "mirror")
        return {"playing": True}

    async def stop(self, _msg):
        if self.code_waiter and not self.code_waiter.done():
            self.code_waiter.set_exception(RuntimeError("Cancelado"))
        await self.disconnect()
        return {"stopped": True}


async def main():
    loop = asyncio.get_running_loop()
    bridge = Bridge(loop)
    pending = set()
    # Si la sesión anterior terminó de golpe, el audio del PC puede haber quedado redirigido.
    await loop.run_in_executor(None, plat.restore_audio_leftovers)
    emit({"event": "ready"})
    # En Windows, leer stdin en un hilo es más fiable que connect_read_pipe.
    while line := await loop.run_in_executor(None, sys.stdin.readline):
        try:
            msg = json.loads(line)
        except ValueError:
            continue
        handler = getattr(bridge, msg.get("cmd", ""), None)
        if handler is None or msg["cmd"].startswith("_"):
            emit({"id": msg.get("id"), "ok": False, "error": f"Comando desconocido: {msg.get('cmd')}"})
            continue

        async def handle(msg=msg, handler=handler):
            try:
                emit({"id": msg.get("id"), "ok": True, "result": await handler(msg)})
            except Exception as exc:  # noqa: BLE001 - se informa a la interfaz
                emit({"id": msg.get("id"), "ok": False, "error": str(exc) or type(exc).__name__})

        pending.add(asyncio.ensure_future(handle()))
        pending.difference_update({t for t in pending if t.done()})
    # stdin cerrado: termina lo pendiente (y deja acabar la reproducción en curso).
    await asyncio.gather(*pending, return_exceptions=True)
    if bridge.task:
        await asyncio.gather(bridge.task, return_exceptions=True)
    await bridge.disconnect()


if __name__ == "__main__":
    asyncio.run(main())
