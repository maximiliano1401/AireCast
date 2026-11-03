# AireCast

Emisor AirPlay de escritorio para Windows y Linux: reproduce contenido y duplica la pantalla en televisores y receptores AirPlay (Roku, Samsung, LG, Apple TV…).

Es una app de Electron (`main.js`, `preload.js`, `index.html`) que se comunica con un puente en Python (`bridge.py`, basado en [pyatv](https://pyatv.dev)). FFmpeg captura y codifica la pantalla (en Wayland captura GStreamer, ver más abajo). Lo que cambia según el sistema está en `platform_win.py`, `platform_linux.py` y `screencast_linux.py` (Python) y en `platformPaths()` (`main.js`).

## Instalar en Linux

Los paquetes de `release/` (ver «Generar el instalador») funcionan en distribuciones de 64 bits con glibc 2.31 o posterior: Debian 11+, Ubuntu 20.04+, Mint 20+, Fedora 32+. Traen el puente, la captura de audio y un FFmpeg propio; lo demás lo pone el sistema.

**Fedora** (.rpm):

```bash
sudo dnf install ./airecast-<versión>.x86_64.rpm
```

**Ubuntu, Mint o Debian** (.deb):

```bash
sudo apt install ./airecast_<versión>_amd64.deb
```

`dnf` y `apt` instalan solos lo que AireCast necesita del sistema: `pactl`/`parec` (`pulseaudio-utils`), GStreamer con `pipewiresrc` y sus plugins base, `xdg-desktop-portal` y `xrandr`. AireCast aparece en el menú de aplicaciones.

**AppImage** (cualquier distribución, sin instalar): necesita FUSE 2 (`fuse-libs` en Fedora, `libfuse2` en Ubuntu/Mint) y las mismas dependencias del sistema, que en este caso hay que instalar a mano (ver «Requisitos» → Linux):

```bash
chmod +x AireCast-<versión>.AppImage
./AireCast-<versión>.AppImage
```

Después, abre los puertos del cortafuegos (ver «Cortafuegos»).

## Requisitos

### Windows

- Windows 10/11
- [Node.js](https://nodejs.org) 20 o superior
- [Python](https://www.python.org) 3.12
- FFmpeg:

  ```powershell
  winget install Gyan.FFmpeg
  ```

### Linux

Probado en Linux Mint 21 (sesión X11, PulseAudio) y Fedora 44 (GNOME en Wayland, PipeWire).

- Node.js 20 o superior. Si tu distribución trae uno más antiguo (Ubuntu 22.04 y Mint 21 traen Node 12), instálalo con [nvm](https://github.com/nvm-sh/nvm):

  ```bash
  nvm install 22
  ```

- Python 3.10 o superior, con `venv`
- FFmpeg con `libx264` (y `x11grab` para X11)
- `pactl` y `parec` (paquete `pulseaudio-utils`; con PipeWire, `pipewire-pulse`)
- Sesión X11: `xrandr`, para detectar el monitor principal
- Sesión Wayland: `xdg-desktop-portal` con el portal de tu escritorio (GNOME, KDE…) y GStreamer con `gst-launch-1.0`, `pipewiresrc` y los plugins base (`videoscale`, `videoconvert`, `videorate`)

En Ubuntu, Mint o Debian:

```bash
sudo apt install python3-venv ffmpeg pulseaudio-utils x11-xserver-utils
# Wayland, además:
sudo apt install gstreamer1.0-tools gstreamer1.0-pipewire gstreamer1.0-plugins-base
```

En Fedora, el FFmpeg oficial (`ffmpeg-free`) no trae `libx264`: usa el de [RPM Fusion](https://rpmfusion.org/Configuration).

```bash
sudo dnf swap ffmpeg-free ffmpeg --allowerasing   # con RPM Fusion (free) activado
sudo dnf install pulseaudio-utils gstreamer1 gstreamer1-plugins-base pipewire-gstreamer
```

## Instalación

Después de clonar el repositorio:

**Windows**

```powershell
# 1. Dependencias de Node (Electron, electron-builder)
npm install

# 2. Entorno virtual de Python con las dependencias del puente
python -m venv .venv
.venv\Scripts\pip install -r requirements.txt

# 3. Ayudante de captura de audio (usa el compilador de C# que ya trae Windows)
npm run build:audiocap
```

**Linux**

```bash
npm install
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
```

En Linux no hay que compilar nada para el audio: `audiocap_linux.py` se ejecuta con el Python del entorno virtual.

> El entorno virtual debe llamarse `.venv` y estar en la raíz del proyecto: en desarrollo, la app ejecuta `bridge.py` con el Python de `.venv` (`.venv\Scripts\python.exe` en Windows y `.venv/bin/python` en Linux).

## Uso en desarrollo

```bash
npm start
```

La primera vez que te conectes a un televisor, la app te pedirá el PIN que aparece en pantalla para emparejarlo.

### Cortafuegos

La TV abre conexiones hacia el PC: responde a la búsqueda (mDNS), consulta la hora durante el duplicado y al enviar vídeo o audio (NTP), usa un canal de control del audio y descarga los vídeos enviados. Si el cortafuegos lo bloquea, la búsqueda no encuentra nada o el duplicado no arranca.

AireCast usa siempre los mismos puertos (`ports.py` y `MEDIA_PORTS` en `main.js`), así que basta con abrir estos, y solo para la red local:

| Puerto | Para qué |
|---|---|
| 5353/udp | Respuestas a la búsqueda (mDNS) |
| 27450-27459/udp | Hora (NTP) y control del audio |
| 27450-27459/tcp | Descarga de los vídeos enviados |

Cada uso toma el primer puerto libre del rango. Si los diez estuvieran ocupados, AireCast usa uno aleatorio y lo avisa en `airplay-debug.log` (o en el registro de la app, para los vídeos); en ese caso hay que liberar alguno para que funcione con el cortafuegos activo.

En los comandos, cambia `192.168.1.0/24` por la red de tu casa.

**ufw** (Ubuntu, Mint):

```bash
sudo ufw allow from 192.168.1.0/24 to any port 5353 proto udp comment 'AireCast mDNS'
sudo ufw allow from 192.168.1.0/24 to any port 27450:27459 proto udp comment 'AireCast'
sudo ufw allow from 192.168.1.0/24 to any port 27450:27459 proto tcp comment 'AireCast'
```

**firewalld** (Fedora). Las reglas van a la zona de tu conexión (`firewall-cmd --get-active-zones`); aquí, la zona por defecto. La zona `FedoraWorkstation` ya deja pasar los puertos 1025-65535, así que con ella solo falta el de mDNS; en zonas más cerradas (`public`, `home`…) hacen falta los tres:

```bash
sudo firewall-cmd --permanent --add-rich-rule='rule family=ipv4 source address=192.168.1.0/24 port port=5353 protocol=udp accept'
sudo firewall-cmd --permanent --add-rich-rule='rule family=ipv4 source address=192.168.1.0/24 port port=27450-27459 protocol=udp accept'
sudo firewall-cmd --permanent --add-rich-rule='rule family=ipv4 source address=192.168.1.0/24 port port=27450-27459 protocol=tcp accept'
sudo firewall-cmd --reload
```

**Firewall de Windows** (PowerShell como administrador). `LocalSubnet` limita las reglas a la red local, y la red Wi-Fi debe estar marcada como privada:

```powershell
New-NetFirewallRule -DisplayName "AireCast mDNS" -Direction Inbound -Protocol UDP -LocalPort 5353 -RemoteAddress LocalSubnet -Profile Private -Action Allow
New-NetFirewallRule -DisplayName "AireCast UDP" -Direction Inbound -Protocol UDP -LocalPort 27450-27459 -RemoteAddress LocalSubnet -Profile Private -Action Allow
New-NetFirewallRule -DisplayName "AireCast TCP" -Direction Inbound -Protocol TCP -LocalPort 27450-27459 -RemoteAddress LocalSubnet -Profile Private -Action Allow
```

Si al abrir AireCast por primera vez Windows preguntó si permitir el acceso y respondiste que no, creó una regla que bloquea el programa (`python.exe` o `bridge.exe`) y pesa más que las de arriba: bórrala en «Firewall de Windows con seguridad avanzada» → «Reglas de entrada».

## Generar el instalador

**Windows**:

```powershell
npm run dist
```

Este comando:

1. Ejecuta `npm run build:bridge` (`build-bridge.ps1`): compila `bridge.py` con PyInstaller en `build/bridge/`, copia `ffmpeg.exe` en `build/ffmpeg/` y compila `audiocap.exe` en `build/audiocap/`.
2. Empaqueta la app con electron-builder en `release/`:
   - `AireCast Setup <versión>.exe`: instalador
   - `AireCast-<versión>-portable.exe`: versión portable

**Linux**:

```bash
npm run dist:linux
```

Necesita Podman (o Docker) y `curl`. Para el .deb y el .rpm, electron-builder usa `fpm` y `rpmbuild`:

```bash
sudo dnf install rpm-build libxcrypt-compat   # Fedora
sudo apt install rpm                          # Ubuntu, Mint o Debian
```

Este comando:

1. Ejecuta `build-linux.sh`:
   - En un contenedor `python:3.12-slim-bullseye` (glibc 2.31, para que los paquetes funcionen también en distribuciones antiguas) compila con PyInstaller `bridge.py` en `build/bridge/` y `audiocap_linux.py` en `build/audiocap/`.
   - Descarga el FFmpeg estático de [johnvansickle.com](https://johnvansickle.com/ffmpeg/) (versión fija, comprobada con SHA-256) en `build/ffmpeg/` y verifica que tenga `libx264` y `x11grab`.
2. Empaqueta la app con electron-builder en `release/`:
   - `AireCast-<versión>.AppImage`
   - `airecast_<versión>_amd64.deb`
   - `airecast-<versión>.x86_64.rpm`

Las dependencias del sistema del .deb y el .rpm están en `build.deb.depends` y `build.rpm.depends` (`package.json`), junto con las que electron-builder pone por defecto para Electron.

## Compatibilidad del duplicado de pantalla

- **TVs con AirPlay 2 de terceros** (Roku, Samsung, LG…): emparejamiento HomeKit, vídeo cifrado con ChaCha20 y audio.
- **Apple TV antiguos** (probado con un AppleTV3,2 real en Windows y Linux): emparejamiento legacy + FairPlay SAP, vídeo cifrado con AES-CTR y audio ALAC cifrado con AES-CBC. Si en el Apple TV la seguridad de AirPlay es "Código en pantalla", la TV muestra un código nuevo en cada conexión y AireCast te pide que lo escribas.

Captura de pantalla: en Windows, todo el escritorio (`gdigrab`); en Linux con X11, el monitor principal (`x11grab`). La imagen se codifica con libx264 por CPU (H.264 baseline, preset `superfast`, CRF 20).

La resolución y los fotogramas por segundo se eligen en la app, debajo de «Enviar contenido». La elección se recuerda y se aplica la próxima vez que compartas la pantalla.

- **Resolución**: 720p, 1080p (por defecto), 1440p, 4K o **Nativa** (la de la pantalla, sin escalar: es la más nítida y la TV la ajusta a la suya). Por encima de 1080p hace falta un receptor que lo admita, como el Apple TV 4K; la Roku y el Apple TV 3 llegan a 1080p. Ojo: una pantalla 16:10 de 1920x1200 en «Nativa» ya pasa de 1080p.
- **fps**: 15, 24, 30 (por defecto) o 60. Más fps dan un movimiento más fluido.

Más resolución y más fps gastan más CPU y más red: si el equipo no da abasto (la imagen se atrasa o va a saltos), baja la resolución o los fps. Medido en un Ryzen de 8 núcleos, 1080p a 30 fps ocupa ~1 núcleo para codificar y unos 5 Mb/s con vídeo en pantalla; el bitrate tiene un tope de ~0,2 bits por píxel (12 Mb/s en 1080p30, 25 como máximo) para no saturar la Wi-Fi. El nivel H.264 lo elige x264 según el tamaño y los fps (4.0 en 1080p30, 4.2 en 1080p60, 5.x por encima), y el tamaño que se anuncia a la TV se lee del propio vídeo (`sps_size()` en `mirror.py`).

**Wayland** (`XDG_SESSION_TYPE=wayland`): `x11grab` no ve las ventanas, así que la captura la hace `screencast_linux.py` con el portal del escritorio (xdg-desktop-portal, ScreenCast) y PipeWire:

- La primera vez, el escritorio pregunta qué pantalla compartir. AireCast guarda el permiso (token de restauración) en `screencast-token`, junto a las credenciales, y las siguientes veces ya no pregunta. Para volver a elegir, borra ese archivo.
- `gst-launch-1.0` lee la pantalla de PipeWire y la escala a la resolución elegida (con «Nativa», al tamaño que informa el portal); FFmpeg la codifica con libx264, como en X11.
- Mientras se duplica, el escritorio muestra su indicador de pantalla compartida. Si lo detienes desde ahí, el duplicado termina.

### Audio solo en la TV

Mientras se duplica la pantalla, el audio suena en la TV y el PC queda en silencio. Para desactivarlo (y que suene en los dos): `MUTE_LOCAL = False` en `mirror.py`.

**Windows**: lo hace `audiocap.exe` (fuente en `audiocap.cs`):

- Captura "todo el audio excepto AireCast" con la captura por proceso de Windows (Windows 10 2004 o posterior). Esa captura ocurre antes del volumen y del silencio de Windows, así que la TV recibe la señal completa sin importar el volumen del PC.
- Silencia la salida del PC al empezar y la restaura al terminar, también si AireCast se cierra de golpe (lo detecta porque se cierra su entrada).
- Si no está disponible (Windows más antiguo o `audiocap.exe` sin compilar), AireCast usa la captura normal y el audio suena también en el PC.

**Linux**: lo hace `audiocap_linux.py`, con PulseAudio (o PipeWire con `pipewire-pulse`):

- Crea una salida virtual llamada «AireCast» (`module-null-sink`), la pone como predeterminada, mueve allí lo que ya está sonando y captura su monitor con `parec`.
- Al terminar vuelve a la salida que estaba antes y le devuelve los flujos, también si AireCast se cierra de golpe.
- Si muere sin restaurar, el puente lo arregla la próxima vez que arranca (la salida anterior queda guardada en `$XDG_RUNTIME_DIR/airecast-audio.json`).
- Con `MUTE_LOCAL = False` captura el monitor de la salida real, sin cambiar nada.

## Licencias de terceros

Los instaladores incluyen un **FFmpeg compilado con libx264**, que por eso se distribuye bajo la **GPL**: en Windows, el de [Gyan](https://www.gyan.dev/ffmpeg/builds/) (GPLv3); en Linux, el estático de [johnvansickle.com](https://johnvansickle.com/ffmpeg/) (GPLv3). FFmpeg va como programa aparte (`resources/ffmpeg/`), que AireCast ejecuta sin enlazarlo; en Linux, junto a él van su licencia (`LICENSE-FFmpeg-GPLv3.txt`) y las notas del autor (`README-FFmpeg.txt`). El código fuente de FFmpeg está en [ffmpeg.org](https://ffmpeg.org/download.html) y el de x264 en [VideoLAN](https://code.videolan.org/videolan/x264).

`fairplay.py` y `fairplay_tables.py` son una traducción a Python del FairPlay SAP de [doubletake](https://github.com/omarroth/doubletake) y se distribuyen bajo **LGPL-3.0-or-later**, como el original. Sus vectores de prueba están en `test_fairplay.py`:

```bash
.venv\Scripts\python -m unittest test_fairplay   # Windows
.venv/bin/python -m unittest test_fairplay       # Linux
```

## Archivos que no están en el repositorio

Se regeneran o se crean solos, así que están en `.gitignore`:

| Carpeta / archivo | Cómo se obtiene |
|---|---|
| `node_modules/` | `npm install` |
| `.venv/` | `python -m venv .venv` + `pip install -r requirements.txt` |
| `build/` | `npm run build:bridge` |
| `release/` | `npm run dist` |
| `credentials.json`, `airplay-debug.log`, `screencast-token` | Los crea la app al ejecutarse (ver abajo) |

### Credenciales de emparejamiento

Cuando emparejas un televisor con su PIN, la app guarda las credenciales en:

| Sistema | Carpeta |
|---|---|
| Windows | `%APPDATA%\AireCast\credentials.json` |
| Linux | `~/.config/AireCast/credentials.json` |

En la misma carpeta se guarda `airplay-debug.log`, el registro del protocolo para diagnosticar fallos, y en Wayland `screencast-token`, el permiso para capturar la pantalla.

Esta carpeta pertenece a cada equipo, no al proyecto. En un equipo nuevo, el archivo no existe al principio y se crea la primera vez que emparejas un televisor. Contiene claves de acceso a tus dispositivos, así que no lo subas al repositorio.
