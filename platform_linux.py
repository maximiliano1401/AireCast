"""Lo específico de Linux (X11 o Wayland) para el duplicado de pantalla (ver mirror.py y platform_win.py)."""

import logging
import os
import re
import subprocess
import sys
from pathlib import Path

import audiocap_linux

_LOGGER = logging.getLogger(__name__)

SUBPROCESS_FLAGS = {}

# miniaudio solo hace loopback con WASAPI: en Linux el audio se captura con audiocap_linux.
HAS_WASAPI_LOOPBACK = False

# "eDP-1 connected primary 1366x768+0+0 (...)"
_XRANDR_MONITOR = re.compile(r"^\S+ connected( primary)? (\d+)x(\d+)\+(\d+)\+(\d+)", re.MULTILINE)


def primary_monitor():
    """Geometría (ancho, alto, x, y) del monitor principal según xrandr, o None si no se sabe.

    Si ninguno está marcado como principal, usa el primero conectado.
    """
    try:
        out = subprocess.run(["xrandr", "--query"], capture_output=True, text=True, timeout=5,
                             env={**os.environ, "LC_ALL": "C"}).stdout
    except (OSError, subprocess.SubprocessError) as exc:
        _LOGGER.warning("No se pudo consultar xrandr: %s", exc)
        return None
    monitors = _XRANDR_MONITOR.findall(out)
    if not monitors:
        return None
    primary = next((m for m in monitors if m[0]), monitors[0])
    return tuple(int(v) for v in primary[1:])


class X11ScreenCapture:
    """Monitor principal con x11grab: FFmpeg lee la pantalla directamente (no hay nada que abrir)."""

    stdin = None

    def __init__(self, width, height, fps):
        self.width, self.height, self.fps = width, height, fps

    async def open(self):
        pass

    def ffmpeg_input_args(self):
        """Entrada de FFmpeg para el monitor principal (todo el escritorio si falla xrandr)."""
        display = os.environ.get("DISPLAY", ":0")
        monitor = primary_monitor()
        if not monitor:
            _LOGGER.warning("No se encontró el monitor principal: se captura todo el escritorio")
            return ["-f", "x11grab", "-framerate", str(self.fps), "-i", display]
        width, height, x, y = monitor
        _LOGGER.debug("Capturando el monitor principal: %dx%d+%d+%d en %s", width, height, x, y, display)
        return ["-f", "x11grab", "-framerate", str(self.fps), "-video_size", f"{width}x{height}",
                "-i", f"{display}+{x},{y}"]

    async def close(self):
        pass


def screen_capture(width, height, fps, state_dir):
    """Wayland: portal + PipeWire (screencast_linux), que pide permiso la primera vez. X11: x11grab."""
    if os.environ.get("XDG_SESSION_TYPE") == "wayland":
        import screencast_linux  # solo Wayland necesita jeepney
        return screencast_linux.WaylandScreenCapture(width, height, fps, state_dir)
    return X11ScreenCapture(width, height, fps)


def audiocap_command(pid, mute):
    """Comando de audiocap_linux: resources/audiocap/audiocap empaquetado, o el script con este Python."""
    if getattr(sys, "frozen", False):
        cmd = [str(Path(sys.executable).parent.parent / "audiocap" / "audiocap")]
    else:
        cmd = [sys.executable, str(Path(audiocap_linux.__file__).resolve())]
    return cmd + [str(pid)] + (["--mute"] if mute else [])


def restore_audio_leftovers():
    """Si un audiocap anterior murió sin restaurar, devuelve la salida de audio a como estaba."""
    try:
        audiocap_linux.restore_leftovers()
    except (OSError, subprocess.SubprocessError) as exc:
        _LOGGER.warning("No se pudo revisar el audio de una sesión anterior: %s", exc)
