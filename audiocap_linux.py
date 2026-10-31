"""audiocap para Linux: captura el audio del sistema con PulseAudio para AireCast.

Uso: python audiocap_linux.py <pid> [--mute]     (mismos argumentos que audiocap.exe; el pid no se usa)

- Con --mute crea una salida virtual (module-null-sink, 44100 Hz estéreo), la pone como
  predeterminada, mueve allí lo que ya suena y captura su monitor: el audio va solo a la TV.
  Sin --mute captura el monitor de la salida actual: suena en el PC y en la TV.
- Escribe PCM 44100 Hz, 16 bits, estéreo, little-endian por stdout (lo produce parec).
- Termina cuando se cierra su stdin (incluido si el puente muere), con SIGTERM/SIGINT/SIGHUP o si
  parec se detiene, y siempre deja la salida predeterminada y los flujos como estaban.
- La salida anterior se guarda en $XDG_RUNTIME_DIR/airecast-audio.json: si este proceso muere sin
  restaurar (SIGKILL), restore_leftovers() lo arregla en el siguiente arranque.
- Escribe "ok" (o "error: …") en stderr al iniciar.

Solo usa la biblioteca estándar: tiene que arrancar rápido (el puente espera el "ok" 5 s).
"""

import json
import os
import signal
import subprocess
import sys
import tempfile
import threading
from pathlib import Path

SINK = "airecast_sink"
RATE = 44100
CHANNELS = 2
STATE_FILE = Path(os.environ.get("XDG_RUNTIME_DIR") or tempfile.gettempdir()) / "airecast-audio.json"


def use_system_libraries():
    """Empaquetado con PyInstaller: que los programas del sistema que lanzamos usen sus bibliotecas.

    El arranque de PyInstaller pone las suyas (de Debian 11) en LD_LIBRARY_PATH, y pactl, parec,
    gst-launch-1.0 o xrandr las heredarían. A este proceso no le afecta: ya las cargó al arrancar.
    Lo usan este script y el puente (que importa este módulo desde platform_linux).
    """
    if not getattr(sys, "frozen", False):
        return
    original = os.environ.pop("LD_LIBRARY_PATH_ORIG", None)
    if original is None:
        os.environ.pop("LD_LIBRARY_PATH", None)
    else:
        os.environ["LD_LIBRARY_PATH"] = original


use_system_libraries()  # antes de _C_ENV, que copia el entorno
_C_ENV = {**os.environ, "LC_ALL": "C"}


def pactl(*args):
    return subprocess.run(["pactl", *args], capture_output=True, text=True, timeout=5,
                          check=True, env=_C_ENV).stdout


def default_sink():
    for line in pactl("info").splitlines():
        if line.startswith("Default Sink:"):
            return line.split(":", 1)[1].strip()
    return None


def sinks():
    """Nombre -> índice de cada salida."""
    rows = (line.split("\t") for line in pactl("list", "short", "sinks").splitlines())
    return {row[1]: row[0] for row in rows if len(row) > 1}


def sink_inputs(sink_index=None):
    """Índices de los flujos que suenan (solo los de esa salida, si se indica)."""
    rows = (line.split("\t") for line in pactl("list", "short", "sink-inputs").splitlines())
    return [row[0] for row in rows if len(row) > 1 and (sink_index is None or row[1] == sink_index)]


def our_modules():
    """Índices de los null-sink de AireCast cargados (puede quedar alguno de una ejecución anterior)."""
    rows = (line.split("\t") for line in pactl("list", "short", "modules").splitlines())
    return [row[0] for row in rows
            if len(row) > 2 and row[1] == "module-null-sink" and f"sink_name={SINK}" in row[2]]


def restore(previous):
    """Vuelve a la salida anterior, le devuelve los flujos y descarga la salida virtual."""
    names = sinks()
    if previous and previous in names and previous != SINK:
        try:
            pactl("set-default-sink", previous)
        except subprocess.SubprocessError:
            pass
        if SINK in names:
            for index in sink_inputs(names[SINK]):
                try:
                    pactl("move-sink-input", index, previous)
                except subprocess.SubprocessError:
                    pass  # el flujo terminó mientras tanto
    for module in our_modules():
        try:
            pactl("unload-module", module)
        except subprocess.SubprocessError:
            pass
    STATE_FILE.unlink(missing_ok=True)


def restore_leftovers():
    """Deshace lo que dejó un audiocap que murió sin restaurar. No hace nada si no quedó nada."""
    try:
        previous = json.loads(STATE_FILE.read_text("utf-8")).get("previous")
    except (OSError, ValueError):
        previous = None
    if previous or our_modules():
        restore(previous)


def mute_local():
    """Crea la salida virtual, la hace predeterminada y le pasa lo que ya suena. Devuelve la anterior."""
    restore_leftovers()
    previous = default_sink()
    STATE_FILE.write_text(json.dumps({"previous": previous}), "utf-8")
    pactl("load-module", "module-null-sink", f"sink_name={SINK}", f"rate={RATE}", f"channels={CHANNELS}",
          "sink_properties=device.description=AireCast")
    pactl("set-default-sink", SINK)
    for index in sink_inputs():
        try:
            pactl("move-sink-input", index, SINK)
        except subprocess.SubprocessError:
            pass
    return previous


def main(args):
    mute = "--mute" in args
    previous = None
    try:
        if mute:
            previous = mute_local()
            source = f"{SINK}.monitor"
        else:
            source = f"{default_sink()}.monitor"
        # parec escribe directamente en nuestro stdout (la tubería hacia el puente).
        parec = subprocess.Popen(["parec", "-d", source, "--format=s16le", f"--rate={RATE}",
                                  f"--channels={CHANNELS}", "--latency-msec=10", "--client-name=AireCast"],
                                 stdin=subprocess.DEVNULL, stdout=sys.stdout.fileno())
    except (OSError, subprocess.SubprocessError) as exc:
        if mute:
            restore(previous)
        detail = exc.stderr.strip() if getattr(exc, "stderr", None) else exc
        print(f"error: {detail}", file=sys.stderr, flush=True)
        return 1

    done = threading.Event()

    def watch_stdin():
        # os.read y no sys.stdin: el lector con búfer aborta el cierre si este hilo sigue esperando.
        try:
            os.read(sys.stdin.fileno(), 1)
        except OSError:
            pass
        done.set()

    def watch_parec():
        parec.wait()
        done.set()

    for target in (watch_stdin, watch_parec):
        threading.Thread(target=target, daemon=True).start()
    for sig in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
        signal.signal(sig, lambda *_: done.set())

    print("ok", file=sys.stderr, flush=True)
    try:
        while not done.wait(0.5):  # con timeout para que las señales se atiendan
            pass
    finally:
        if parec.poll() is None:
            parec.terminate()
            try:
                parec.wait(2)
            except subprocess.TimeoutExpired:
                parec.kill()
        if mute:
            restore(previous)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
