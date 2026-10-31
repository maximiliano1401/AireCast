# AireCast

Emisor AirPlay de escritorio para Windows y Linux: reproduce contenido y duplica la pantalla en televisores y receptores AirPlay (Roku, Samsung, LG, Apple TV…).

Es una app de Electron (`main.js`, `preload.js`, `index.html`) que se comunica con un puente en Python (`bridge.py`, basado en [pyatv](https://pyatv.dev)). FFmpeg captura y codifica la pantalla. Lo que cambia según el sistema está en `platform_win.py` y `platform_linux.py` (Python) y en `platformPaths()` (`main.js`).

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

Probado en Linux Mint 21 (sesión X11, PulseAudio). Wayland todavía no es compatible.

- Node.js 20 o superior. Si tu distribución trae uno más antiguo (Ubuntu 22.04 y Mint 21 traen Node 12), instálalo con [nvm](https://github.com/nvm-sh/nvm):

  ```bash
  nvm install 22
  ```

- Python 3.10 o superior, con `venv`
- FFmpeg con `x11grab` y `libx264`
- `pactl` y `parec` (paquete `pulseaudio-utils`; con PipeWire, `pipewire-pulse`)
- `xrandr`, para detectar el monitor principal

En Ubuntu, Mint o Debian:

```bash
sudo apt install python3-venv ffmpeg pulseaudio-utils x11-xserver-utils
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

### Cortafuegos (Linux)

La TV abre conexiones hacia el PC: responde a la búsqueda (mDNS por unicast), consulta la hora durante el duplicado (NTP) y descarga los vídeos enviados. Si el cortafuegos bloquea lo entrante, la búsqueda no encuentra nada y el duplicado no arranca. Por ahora los puertos son aleatorios, así que hay que permitir lo que llegue desde la red local:

```bash
# ufw (Ubuntu, Mint)
sudo ufw allow from 192.168.1.0/24 comment 'AireCast'

# firewalld (Fedora)
sudo firewall-cmd --zone=trusted --add-source=192.168.1.0/24
```

Cambia `192.168.1.0/24` por la red de tu casa.

## Generar el instalador

Por ahora el instalador solo se genera para Windows:

```powershell
npm run dist
```

Este comando:

1. Ejecuta `npm run build:bridge` (`build-bridge.ps1`): compila `bridge.py` con PyInstaller en `build/bridge/`, copia `ffmpeg.exe` en `build/ffmpeg/` y compila `audiocap.exe` en `build/audiocap/`.
2. Empaqueta la app con electron-builder en `release/`:
   - `AireCast Setup <versión>.exe`: instalador
   - `AireCast-<versión>-portable.exe`: versión portable

Los paquetes de Linux (AppImage, .deb y .rpm) están pendientes.

## Compatibilidad del duplicado de pantalla

- **TVs con AirPlay 2 de terceros** (Roku, Samsung, LG…): emparejamiento HomeKit, vídeo cifrado con ChaCha20 y audio.
- **Apple TV antiguos** (probado con un AppleTV3,2 real en Windows y Linux): emparejamiento legacy + FairPlay SAP, vídeo cifrado con AES-CTR y audio ALAC cifrado con AES-CBC. Si en el Apple TV la seguridad de AirPlay es "Código en pantalla", la TV muestra un código nuevo en cada conexión y AireCast te pide que lo escribas.

Captura de pantalla: en Windows, todo el escritorio (`gdigrab`); en Linux, el monitor principal (`x11grab`). La imagen se codifica con libx264 por CPU a 30 fps, así que en equipos poco potentes puede perder fotogramas.

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
| `credentials.json`, `airplay-debug.log` | Los crea la app al ejecutarse (ver abajo) |

### Credenciales de emparejamiento

Cuando emparejas un televisor con su PIN, la app guarda las credenciales en:

| Sistema | Carpeta |
|---|---|
| Windows | `%APPDATA%\AireCast\credentials.json` |
| Linux | `~/.config/AireCast/credentials.json` |

En la misma carpeta se guarda `airplay-debug.log`, el registro del protocolo para diagnosticar fallos.

Esta carpeta pertenece a cada equipo, no al proyecto. En un equipo nuevo, el archivo no existe al principio y se crea la primera vez que emparejas un televisor. Contiene claves de acceso a tus dispositivos, así que no lo subas al repositorio.
