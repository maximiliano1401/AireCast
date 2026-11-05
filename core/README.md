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
AireCast.Cli mirror <nombre, id o dirección> [--resolution 1080p] [--fps 30] [--source screen|test] [--no-audio] [--network normal|unstable]
```

## Requisitos para ejecutarla

- **FFmpeg** con libx264: junto al ejecutable o en el PATH (en Windows también vale `winget install Gyan.FFmpeg`).
- **Linux**: `pactl` y `parec`, y en Wayland `xdg-desktop-portal` y GStreamer con `pipewiresrc`, como la versión Python. En X11, `xrandr`.
- **Windows**: `audiocap.exe` junto al ejecutable para el audio. El build lo compila si falta (`audiocap.cs`, con el `csc.exe` que trae Windows) y lo copia al ejecutar y al publicar; también puedes compilarlo a mano con `npm run build:audiocap` (lo deja en `build/audiocap/`). Si no está, el duplicado avisa en el registro y la TV no recibe audio. Solo se compila en un Windows: un `publish.sh` desde Linux no lo incluye.

Usa los mismos archivos que la versión Python (`credentials.json` y `screencast-token`), así que los emparejamientos existentes siguen valiendo.

## Resolución

Los Apple TV antiguos admiten hasta 1080p: la app solo les ofrece 720p y 1080p. Además no informan del tamaño de su pantalla y presentan la imagen sin escalar, así que se les pide ajustarla a 1280x720 (ver `LegacyDisplayWidth`); con la TV a 1080p la imagen podría verse más pequeña, sin haberlo comprobado.

## Red inestable

Con un Wi-Fi saturado o en 2,4 GHz, los paquetes de audio llegan tarde (se han medido picos de ~140 ms hasta el router) y la TV deja un hueco: el sonido se entrecorta. El audio sale con un margen de solo 25 ms, que la TV usa como búfer.

La opción **Red** de la app (o `--network unstable` en la consola) sube ese margen a 150 ms (`NetworkProfile.UnstableAudioMarginMs`) y retrasa el vídeo lo mismo, para que sigan sincronizados: la hora de cada fotograma se pospone la diferencia (125 ms con el margen normal de 25 ms).

- **«Buena» (por defecto) no cambia nada**: mismo margen, mismos mensajes y vídeo sin retraso. Solo «Inestable» tiene efecto, y se queda guardada la elección.
- **Solo en receptores con HomeKit** (Roku, Samsung, LG, Apple TV 4K…). Los Apple TV antiguos ya llevan su propio ajuste y no admiten más de ~100 ms de retraso en el vídeo, así que la opción se desactiva.
- **El coste es retraso**: ~150 ms más entre lo que haces y lo que ves y oyes en la TV. Para ver vídeo no se nota; para jugar, sí.
- **Se ha comprobado con el receptor simulado** (que se anuncia el margen mayor y que el fotograma lleva la hora retrasada), pero la sincronía con una TV real depende de que respete esa hora. Si con «Inestable» ves la imagen adelantada o atrasada respecto al sonido, el ajuste está en `UnstableAudioMarginMs`.

Aparte, el envío del audio pide a Windows un temporizador de 1 ms mientras dura la sesión (`TimerResolution`). Sin él, el hilo despierta cada ~15,6 ms y los paquetes de 8 ms salen de dos en dos; con él despierta cada ~2,6 ms y salen uno a uno. No añade retraso.

## Pendiente respecto a la versión Python

- Enviar vídeo y audio a la TV (nunca funcionaron con el Apple TV 3).
- Captura de audio de Windows sin `audiocap.exe`.

## Licencias

`AireCast.Core/FairPlay/` es una traducción de `fairplay.py`, a su vez de [doubletake](https://github.com/omarroth/doubletake), y se distribuye bajo **LGPL-3.0-or-later**. Dependencias: BouncyCastle (MIT), Tmds.DBus.Protocol (MIT) y Avalonia (MIT).
