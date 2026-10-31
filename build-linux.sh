#!/usr/bin/env bash
# Prepara los recursos que electron-builder incluye en los paquetes de Linux (como build-bridge.ps1
# en Windows):
#   build/bridge/   -> bridge   (puente Python compilado con PyInstaller)
#   build/audiocap/ -> audiocap (audiocap_linux.py compilado con PyInstaller)
#   build/ffmpeg/   -> ffmpeg estático de johnvansickle.com (GPLv3, con libx264 y x11grab) y su licencia
#
# PyInstaller corre en un contenedor python:3.12-slim-bullseye (glibc 2.31), así que los binarios
# funcionan en distribuciones con glibc 2.31 o posterior (Debian 11+, Ubuntu 20.04+, Mint 20+,
# Fedora 32+). Usa Podman, o Docker si no hay Podman (CONTAINER_ENGINE=docker para forzarlo).
set -euo pipefail
cd "$(dirname "$0")"

IMAGE=docker.io/library/python:3.12-slim-bullseye
PYINSTALLER_VERSION=6.22.3
FFMPEG_VERSION=7.0.2
FFMPEG_SHA256=abda8d77ce8309141f83ab8edf0596834087c52467f6badf376a6a2a4c87cf67

ENGINE=${CONTAINER_ENGINE:-$(command -v podman || command -v docker || true)}
[ -n "$ENGINE" ] || { echo "Hace falta Podman o Docker" >&2; exit 1; }

rm -rf build
mkdir -p build

# --- Puente y audiocap con PyInstaller, en el contenedor ---------------------------------------
# El proyecto se monta de solo lectura y solo build/ con escritura. label=disable: que SELinux
# deje leer el proyecto sin reetiquetarlo. Con Docker (que corre como root) se devuelve build/ al
# usuario al terminar; con Podman sin root no hace falta.
OWNER=""
[[ "$(basename "$ENGINE")" == docker* ]] && OWNER="$(id -u):$(id -g)"
"$ENGINE" run --rm --security-opt label=disable \
    -v "$PWD":/src:ro -v "$PWD/build":/src/build -v airecast-pip-cache:/root/.cache/pip \
    -w /src -e OWNER="$OWNER" -e PYINSTALLER_VERSION="$PYINSTALLER_VERSION" \
    "$IMAGE" bash -euo pipefail -c '
        apt-get update -qq && apt-get install -y -qq --no-install-recommends binutils >/dev/null
        pip install --quiet --root-user-action=ignore --disable-pip-version-check \
            -r requirements.txt "pyinstaller==$PYINSTALLER_VERSION"
        common=(--onedir --noconfirm --clean --distpath build --workpath build/pyinstaller --specpath build/pyinstaller)
        # screencast_linux se importa solo en Wayland (dentro de una función): se nombra para asegurarlo.
        pyinstaller bridge.py --name bridge "${common[@]}" \
            --collect-all pyatv --collect-all miniaudio --collect-submodules zeroconf \
            --hidden-import screencast_linux --collect-submodules jeepney
        pyinstaller audiocap_linux.py --name audiocap "${common[@]}"
        rm -rf build/pyinstaller
        [ -z "$OWNER" ] || chown -R "$OWNER" build
    '

# --- FFmpeg estático ---------------------------------------------------------------------------
# Las versiones nuevas están en releases/ y las anteriores pasan a old-releases/.
archive="ffmpeg-$FFMPEG_VERSION-amd64-static.tar.xz"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
for dir in releases old-releases; do
    curl -fsSL -o "$tmp/$archive" "https://johnvansickle.com/ffmpeg/$dir/$archive" && break
done
[ -s "$tmp/$archive" ] || { echo "No se pudo descargar $archive" >&2; exit 1; }
echo "$FFMPEG_SHA256  $tmp/$archive" | sha256sum -c --quiet -
tar -xf "$tmp/$archive" -C "$tmp"
mkdir -p build/ffmpeg
cp "$tmp/ffmpeg-$FFMPEG_VERSION-amd64-static/ffmpeg" build/ffmpeg/ffmpeg
cp "$tmp/ffmpeg-$FFMPEG_VERSION-amd64-static/GPLv3.txt" build/ffmpeg/LICENSE-FFmpeg-GPLv3.txt
cp "$tmp/ffmpeg-$FFMPEG_VERSION-amd64-static/readme.txt" build/ffmpeg/README-FFmpeg.txt
grep -q " libx264 " <<<"$(build/ffmpeg/ffmpeg -hide_banner -encoders 2>/dev/null)" || { echo "FFmpeg sin libx264" >&2; exit 1; }
grep -q " x11grab " <<<"$(build/ffmpeg/ffmpeg -hide_banner -devices 2>/dev/null)" || { echo "FFmpeg sin x11grab" >&2; exit 1; }

echo "Recursos listos en build/bridge, build/audiocap y build/ffmpeg"
