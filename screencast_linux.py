"""Captura de pantalla en Wayland: portal ScreenCast (xdg-desktop-portal) + PipeWire + GStreamer.

1. Pide al portal una sesión que capture un monitor. La primera vez el escritorio muestra su diálogo
   de permiso; la respuesta trae un token de restauración que se guarda para no volver a preguntar
   (es de un solo uso: el portal entrega uno nuevo en cada sesión).
2. OpenPipeWireRemote devuelve un descriptor de PipeWire con acceso solo a ese flujo.
3. gst-launch-1.0 (pipewiresrc) lee el flujo, lo escala con bandas negras y entrega vídeo crudo I420
   a ritmo constante por una tubería; FFmpeg lo codifica igual que en X11 (ver mirror_screen).

La sesión del portal dura mientras siga abierta su conexión D-Bus. jeepney es Python puro, pero solo
pasa descriptores con su API bloqueante: el diálogo se espera en un hilo aparte.
"""

import asyncio
import logging
import os
import secrets
from pathlib import Path

from jeepney import DBusAddress, MatchRule, Properties, new_method_call
from jeepney.bus_messages import message_bus
from jeepney.io.blocking import open_dbus_connection
from jeepney.wrappers import unwrap_msg

_LOGGER = logging.getLogger(__name__)

PORTAL = DBusAddress("/org/freedesktop/portal/desktop", bus_name="org.freedesktop.portal.Desktop",
                     interface="org.freedesktop.portal.ScreenCast")
SOURCE_MONITOR = 1
CURSOR_EMBEDDED = 2  # el cursor va dibujado en la imagen
PERSIST_UNTIL_REVOKED = 2  # el token sirve hasta que el usuario retire el permiso
RESPONSE_CANCELLED = 1
REQUEST_TIMEOUT = 30
START_TIMEOUT = 120  # lo que el usuario tiene para responder al diálogo (como el código de la TV)


class PortalError(RuntimeError):
    pass


def _token():
    return "airecast_" + secrets.token_hex(8)


class ScreenCastPortal:
    """Sesión del portal ScreenCast para un monitor. Métodos bloqueantes."""

    def __init__(self, token_file):
        self.token_file = Path(token_file)
        self.conn = None
        self.session = None
        self.size = None  # (ancho, alto) de la pantalla elegida, si el portal lo informa

    def open(self):
        """Devuelve (descriptor de PipeWire, id del nodo). Bloquea mientras se responde al diálogo."""
        self.conn = open_dbus_connection(bus="SESSION", enable_fds=True)
        try:
            self.session = self._request("CreateSession", "a{sv}", {
                "session_handle_token": ("s", _token())})["session_handle"][1]
            options = {"types": ("u", SOURCE_MONITOR), "multiple": ("b", False)}
            if self._property("AvailableCursorModes") & CURSOR_EMBEDDED:
                options["cursor_mode"] = ("u", CURSOR_EMBEDDED)
            if self._property("version") >= 4:
                options["persist_mode"] = ("u", PERSIST_UNTIL_REVOKED)
                token = self._load_token()
                if token:
                    options["restore_token"] = ("s", token)
            self._request("SelectSources", "oa{sv}", self.session, options)
            results = self._request("Start", "osa{sv}", self.session, "", {}, timeout=START_TIMEOUT)
            if "restore_token" in results:
                self._save_token(results["restore_token"][1])
            streams = results.get("streams", ("", []))[1]
            if not streams:
                raise PortalError("El portal no devolvió ninguna pantalla")
            node, props = streams[0]
            _LOGGER.debug("Portal: nodo PipeWire %d, %s", node, {k: v[1] for k, v in props.items()})
            if "size" in props:
                self.size = tuple(props["size"][1])
            reply = self.conn.send_and_get_reply(
                new_method_call(PORTAL, "OpenPipeWireRemote", "oa{sv}", (self.session, {})),
                timeout=REQUEST_TIMEOUT)
            return unwrap_msg(reply)[0].to_raw_fd(), node
        except BaseException:
            self.close()
            raise

    def close(self):
        """Cierra la sesión del portal (el escritorio quita su indicador de captura)."""
        if not self.conn:
            return
        if self.session:
            session = DBusAddress(self.session, bus_name=PORTAL.bus_name, interface="org.freedesktop.portal.Session")
            try:
                self.conn.send(new_method_call(session, "Close"))
            except OSError:
                pass
        self.conn.close()
        self.conn = self.session = None

    def _property(self, name):
        reply = self.conn.send_and_get_reply(Properties(PORTAL).get(name), timeout=REQUEST_TIMEOUT)
        return unwrap_msg(reply)[0][1]

    def _request(self, method, signature, *args, timeout=REQUEST_TIMEOUT):
        """Llama a un método que responde con la señal Response de un objeto Request; devuelve sus resultados.

        El último argumento son las opciones (a{sv}): se les añade el handle_token, que fija la ruta del
        Request, para suscribirse a la respuesta antes de llamar.
        """
        token = _token()
        sender = self.conn.unique_name[1:].replace(".", "_")
        rule = MatchRule(type="signal", interface="org.freedesktop.portal.Request", member="Response",
                         path=f"/org/freedesktop/portal/desktop/request/{sender}/{token}")
        *args, options = args
        with self.conn.filter(rule) as responses:
            unwrap_msg(self.conn.send_and_get_reply(message_bus.AddMatch(rule), timeout=REQUEST_TIMEOUT))
            unwrap_msg(self.conn.send_and_get_reply(new_method_call(
                PORTAL, method, signature, (*args, {**options, "handle_token": ("s", token)})),
                timeout=REQUEST_TIMEOUT))
            try:
                code, results = self.conn.recv_until_filtered(responses, timeout=timeout).body
            except TimeoutError:
                raise PortalError("No se respondió a tiempo al permiso para compartir la pantalla") from None
        if code == RESPONSE_CANCELLED:
            raise PortalError("Se canceló el permiso para compartir la pantalla")
        if code != 0:
            raise PortalError(f"El portal de captura rechazó {method} (respuesta {code})")
        return results

    def _load_token(self):
        try:
            return self.token_file.read_text("utf-8").strip() or None
        except OSError:
            return None

    def _save_token(self, token):
        try:
            self.token_file.parent.mkdir(parents=True, exist_ok=True)
            self.token_file.write_text(token, "utf-8")
        except OSError as exc:
            _LOGGER.warning("No se pudo guardar el token del portal: %s", exc)


class WaylandScreenCapture:
    """Monitor elegido en el portal, escalado a width x height; FFmpeg lo lee crudo por su stdin."""

    def __init__(self, width, height, fps, state_dir):
        self.width, self.height, self.fps = width, height, fps
        self.portal = ScreenCastPortal(Path(state_dir) / "screencast-token")
        self.stdin = None  # extremo de lectura de la tubería gst-launch -> FFmpeg
        self._gst = None
        self._log_task = None

    async def open(self):
        fd, node = await asyncio.get_running_loop().run_in_executor(None, self.portal.open)
        if self.width is None:  # resolución nativa: la de la pantalla, en números pares (yuv420p)
            if not self.portal.size:
                os.close(fd)
                self.portal.close()
                raise RuntimeError("El portal no informó del tamaño de la pantalla: elige otra resolución")
            self.width, self.height = (value // 2 * 2 for value in self.portal.size)
        read, write = os.pipe()
        try:
            self._gst = await asyncio.create_subprocess_exec(
                "gst-launch-1.0", "-q", *self._pipeline(fd, node),
                stdin=asyncio.subprocess.DEVNULL, stdout=write, stderr=asyncio.subprocess.PIPE,
                pass_fds=(fd,))
        except BaseException as exc:
            os.close(read)
            self.portal.close()
            if isinstance(exc, FileNotFoundError):
                raise RuntimeError("No se encontró gst-launch-1.0 (GStreamer)") from None
            raise
        finally:
            # Solo gst-launch debe tener abiertos el descriptor de PipeWire y la escritura de la
            # tubería: así FFmpeg ve el fin del flujo si gst-launch termina.
            os.close(write)
            os.close(fd)
        self.stdin = read
        self._log_task = asyncio.ensure_future(self._log_stderr())

    def _pipeline(self, fd, node):
        caps = (f"video/x-raw,format=I420,width={self.width},height={self.height},"
                f"framerate={self.fps}/1,pixel-aspect-ratio=1/1")
        # GNOME solo envía fotogramas cuando cambia la pantalla: keepalive-time repite el último para
        # que el flujo no se detenga con la imagen quieta. Se escala antes de convertir el color, que
        # así trabaja con menos píxeles.
        return ["pipewiresrc", f"fd={fd}", f"path={node}", "do-timestamp=true",
                f"keepalive-time={1000 // self.fps}",
                "!", "videoscale", "n-threads=0",
                "!", "videoconvert", "n-threads=0",
                "!", "videorate",
                "!", caps,
                "!", "fdsink", "fd=1", "sync=false"]

    def ffmpeg_input_args(self):
        return ["-f", "rawvideo", "-pix_fmt", "yuv420p", "-video_size", f"{self.width}x{self.height}",
                "-framerate", str(self.fps), "-i", "pipe:0"]

    async def _log_stderr(self):
        while line := await self._gst.stderr.readline():
            _LOGGER.warning("gst-launch: %s", line.decode(errors="replace").rstrip())
        _LOGGER.debug("gst-launch terminó con código %s", await self._gst.wait())

    async def close(self):
        if self._gst and self._gst.returncode is None:
            self._gst.terminate()
            try:
                await asyncio.wait_for(self._gst.wait(), 2)
            except asyncio.TimeoutError:
                self._gst.kill()
                await self._gst.wait()
        if self._log_task:
            await self._log_task
            self._log_task = None
        if self.stdin is not None:
            os.close(self.stdin)
            self.stdin = None
        self.portal.close()
