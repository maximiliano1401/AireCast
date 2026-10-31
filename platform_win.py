"""Lo específico de Windows para el duplicado de pantalla (ver mirror.py y platform_linux.py)."""

import sys
from pathlib import Path

# CREATE_NO_WINDOW: que FFmpeg y audiocap no abran una consola.
SUBPROCESS_FLAGS = {"creationflags": 0x08000000}

# Captura del audio del sistema con WASAPI loopback (miniaudio) si no hay audiocap.
HAS_WASAPI_LOOPBACK = True


class GdiScreenCapture:
    """Escritorio completo con gdigrab: FFmpeg lee la pantalla directamente (no hay nada que abrir)."""

    stdin = None

    def __init__(self, width, height, fps):
        self.width, self.height, self.fps = width, height, fps

    async def open(self):
        pass

    def ffmpeg_input_args(self):
        return ["-f", "gdigrab", "-framerate", str(self.fps), "-i", "desktop"]

    async def close(self):
        pass


def screen_capture(width, height, fps, state_dir):
    """Captura de pantalla para mirror_screen (state_dir solo lo usa Wayland, para su token)."""
    return GdiScreenCapture(width, height, fps)


def audiocap_path():
    """audiocap.exe: junto al puente empaquetado (resources/audiocap) o en build/ en desarrollo."""
    if getattr(sys, "frozen", False):
        exe = Path(sys.executable).parent.parent / "audiocap" / "audiocap.exe"
    else:
        exe = Path(__file__).parent / "build" / "audiocap" / "audiocap.exe"
    return exe if exe.exists() else None


def audiocap_command(pid, mute):
    """Comando de audiocap.exe (captura por proceso, ver audiocap.cs), o None si no está compilado."""
    exe = audiocap_path()
    if not exe:
        return None
    return [str(exe), str(pid)] + (["--mute"] if mute else [])


def restore_audio_leftovers():
    """audiocap.exe restaura el silencio del PC él mismo: no queda nada que limpiar al arrancar."""
