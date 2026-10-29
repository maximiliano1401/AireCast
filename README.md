# AireCast

Emisor AirPlay de escritorio para Windows: reproduce contenido y duplica la pantalla en televisores y receptores AirPlay (Roku, Samsung, LG…).

Es una app de Electron (`main.js`, `preload.js`, `index.html`) que se comunica con un puente en Python (`bridge.py`, basado en [pyatv](https://pyatv.dev)). FFmpeg captura y codifica la pantalla.

## Requisitos

- Windows 10/11
- [Node.js](https://nodejs.org) 20 o superior
- [Python](https://www.python.org) 3.12
- FFmpeg:

  ```powershell
  winget install Gyan.FFmpeg
  ```

## Instalación

Después de clonar el repositorio:

```powershell
# 1. Dependencias de Node (Electron, electron-builder)
npm install

# 2. Entorno virtual de Python con las dependencias del puente
python -m venv .venv
.venv\Scripts\pip install -r requirements.txt
```

> El entorno virtual debe llamarse `.venv` y estar en la raíz del proyecto: en desarrollo, la app ejecuta `.venv\Scripts\python.exe bridge.py`.

## Uso en desarrollo

```powershell
npm start
```

La primera vez que te conectes a un televisor, la app te pedirá el PIN que aparece en pantalla para emparejarlo.

## Generar el instalador

```powershell
npm run dist
```

Este comando:

1. Ejecuta `npm run build:bridge` (`build-bridge.ps1`): compila `bridge.py` con PyInstaller en `build/bridge/` y copia `ffmpeg.exe` en `build/ffmpeg/`.
2. Empaqueta la app con electron-builder en `release/`:
   - `AireCast Setup <versión>.exe`: instalador
   - `AireCast-<versión>-portable.exe`: versión portable

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

```
%APPDATA%\AireCast\credentials.json
```

En la misma carpeta se guarda `airplay-debug.log`, el registro del protocolo para diagnosticar fallos.

Esta carpeta pertenece a cada equipo, no al proyecto. En un equipo nuevo, el archivo no existe al principio y se crea la primera vez que emparejas un televisor. Contiene claves de acceso a tus dispositivos, así que no lo subas al repositorio.
