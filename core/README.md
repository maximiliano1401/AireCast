# Núcleo de AireCast en C#

Reescritura de AireCast sobre .NET 10, con una sola base de código para Windows, Linux y, más adelante, Android.

| Proyecto | Qué es |
|---|---|
| `AireCast.Core` | El protocolo y la captura: búsqueda, emparejamiento, FairPlay, sesión, vídeo y audio |
| `AireCast.App` | La app de escritorio (Avalonia) |
| `AireCast.Cli` | Consola para probar cada parte por separado |
| `AireCast.Core.Tests` | Pruebas, con bytes generados por la versión Python para garantizar que hablan igual |

## Compilar y probar

```bash
dotnet test                                    # pruebas
dotnet run --project AireCast.App              # la app
dotnet run --project AireCast.Cli -- discover  # la consola
./publish.sh                                   # ejecutables autónomos en dist/
```

## Consola

```bash
AireCast.Cli discover [segundos]
AireCast.Cli verify <nombre, id o dirección>
AireCast.Cli pair <nombre, id o dirección>
AireCast.Cli capture [--seconds 5] [--resolution 1080p] [--fps 30] [--source screen|test]
AireCast.Cli mirror <nombre, id o dirección> [--resolution 1080p] [--fps 30] [--source screen|test] [--no-audio]
```

## Requisitos para ejecutarla

- **FFmpeg** con libx264: junto al ejecutable o en el PATH (en Windows también vale `winget install Gyan.FFmpeg`).
- **Linux**: `pactl` y `parec`, y en Wayland `xdg-desktop-portal` y GStreamer con `pipewiresrc`, como la versión Python. En X11, `xrandr`.
- **Windows**: `audiocap.exe` junto al ejecutable para el audio (`npm run build:audiocap` lo deja en `build/audiocap/`); sin él, la TV recibe silencio.

Usa los mismos archivos que la versión Python (`credentials.json` y `screencast-token`), así que los emparejamientos existentes siguen valiendo.

## Resolución

Los Apple TV antiguos admiten hasta 1080p: la app solo les ofrece 720p y 1080p. Además no informan del tamaño de su pantalla y presentan la imagen sin escalar, así que se les pide ajustarla a 1280x720 (ver `LegacyDisplayWidth`); con la TV a 1080p la imagen podría verse más pequeña, sin haberlo comprobado.

## Pendiente respecto a la versión Python

- Enviar vídeo y audio a la TV (nunca funcionaron con el Apple TV 3).
- Captura de audio de Windows sin `audiocap.exe`.

## Licencias

`AireCast.Core/FairPlay/` es una traducción de `fairplay.py`, a su vez de [doubletake](https://github.com/omarroth/doubletake), y se distribuye bajo **LGPL-3.0-or-later**. Dependencias: BouncyCastle (MIT), Tmds.DBus.Protocol (MIT) y Avalonia (MIT).
