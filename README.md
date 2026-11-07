<p align="center"><img src="assets/logo-256.png" alt="AireCast" width="128"></p>

# AireCast

Duplica la pantalla de tu PC o tu teléfono en televisores y receptores AirPlay, con audio. Funciona en Windows, Linux y Android.

Probado en Windows 11, Fedora y Linux Mint, con Apple TV 3, Apple TV 4K y Roku.

## Instalar

Descarga el instalador para tu sistema desde [Releases](../../releases):

| Sistema | Archivo |
|---|---|
| Windows | `AireCast-<versión>-Setup.exe` |
| Fedora | `airecast-<versión>.x86_64.rpm` |
| Ubuntu, Mint, Debian | `airecast_<versión>_amd64.deb` |
| Otras distribuciones | `AireCast-<versión>-x86_64.AppImage` |
| Android 10 o superior | `AireCast-<versión>.apk` |

Si no aparecen dispositivos, revisa el [cortafuegos](docs/TECNICO.md#cortafuegos).

## Usar

1. Abre AireCast y elige tu TV. Si pide PIN, escribe el que muestra.
2. Pulsa **Compartir pantalla**.

La resolución, los fotogramas por segundo y la codificación con la GPU están en **Ajustes** (el engranaje).

## Desarrollar

Necesitas el SDK de .NET 10. Desde `core/`:

```bash
dotnet test                        # pruebas
dotnet run --project AireCast.App  # la app
```

Más detalles en [docs/TECNICO.md](docs/TECNICO.md). La versión anterior, con Electron y Python, está en la rama `legacy`.

## Licencia

AireCast se distribuye bajo la [GPL-3.0-or-later](LICENSE). Agradecimientos y licencias de terceros en [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

AireCast no está afiliado a Apple Inc. AirPlay y Apple TV son marcas de Apple Inc.
