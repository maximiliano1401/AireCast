# Avisos de terceros

AireCast se distribuye bajo la licencia GPL-3.0-or-later (ver [LICENSE](LICENSE)). No contiene código ni claves de Apple.

## Proyectos en los que se basa

| Proyecto | Licencia | Cómo se usó |
|---|---|---|
| [doubletake](https://github.com/omarroth/doubletake) (omarroth) | LGPL-3.0-or-later | `core/AireCast.Core/FairPlay/` es una traducción de su FairPlay SAP y conserva esa licencia (ver su [LICENSE](core/AireCast.Core/FairPlay/LICENSE)). También fue la referencia para el duplicado en los Apple TV antiguos y para la captura en Wayland. |
| [pyatv](https://github.com/postlund/pyatv) (Pierre Ståhl) | MIT | Referencia del protocolo: emparejamiento, RTSP, audio y tiempos. La versión anterior de AireCast se basaba en esta biblioteca. |
| [WinPlay](https://github.com/dineshdhotrad/WinPlay) (Dinesh Dhotrad) | GPL-3.0-or-later | Consultado para entender el protocolo y como inspiración de la arquitectura. No se copió código. |

## Bibliotecas

| Biblioteca | Licencia | Para qué |
|---|---|---|
| [.NET](https://github.com/dotnet/runtime) | MIT | Plataforma |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | MIT | Interfaz |
| [BouncyCastle](https://github.com/bcgit/bc-csharp) | MIT | Cifrado |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) | MIT | Portal de captura en Wayland |
| [Inter](https://github.com/rsms/inter) | OFL-1.1 | Tipografía |

## Herramientas externas

| Herramienta | Licencia | Para qué |
|---|---|---|
| [FFmpeg](https://ffmpeg.org) con [x264](https://www.videolan.org/developers/x264.html) | GPL | Codificación H.264. Los instaladores incluyen un FFmpeg GPL como programa aparte, junto a su licencia. Su código fuente está en [ffmpeg.org](https://ffmpeg.org/download.html) y el de x264 en [VideoLAN](https://code.videolan.org/videolan/x264). |
| [GStreamer](https://gstreamer.freedesktop.org) y [PipeWire](https://pipewire.org) | LGPL / MIT | Captura en Wayland (del sistema) |
| [PulseAudio](https://www.freedesktop.org/wiki/Software/PulseAudio/) | LGPL | `pactl` y `parec`, captura de audio en Linux (del sistema) |
