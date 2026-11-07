# Detalles técnicos de AireCast

## Consola

```bash
AireCast.Cli discover [segundos]
AireCast.Cli verify <nombre, id o dirección>
AireCast.Cli pair <nombre, id o dirección>
AireCast.Cli capture [--seconds 5] [--resolution 1080p] [--fps 30] [--source screen|test]
AireCast.Cli mirror <nombre, id o dirección> [--resolution 1080p] [--fps 30] [--source screen|test] [--no-audio] [--network normal|unstable]
```

## Requisitos

- **FFmpeg** con libx264: junto al ejecutable o en el PATH (en Windows también vale `winget install Gyan.FFmpeg`).
- **Linux**: `pactl` y `parec`, y en Wayland `xdg-desktop-portal` y GStreamer con `pipewiresrc`. En X11, `xrandr`.
- **Windows**: `audiocap.exe` junto al ejecutable para el audio. El build lo compila si falta (`audiocap.cs`, con el `csc.exe` que trae Windows) y lo copia al ejecutar y al publicar; también puedes compilarlo a mano con `npm run build:audiocap` (lo deja en `build/audiocap/`). Si no está, el duplicado avisa en el registro y la TV no recibe audio. Solo se compila en un Windows: un `publish.sh` desde Linux no lo incluye.

Las credenciales de emparejamiento y el permiso de captura de Wayland se guardan en `~/.config/AireCast/` en Linux y en `%APPDATA%\AireCast\` en Windows. Son claves de acceso a tus dispositivos: no las subas al repositorio.

## Cortafuegos

La TV abre conexiones hacia el PC: responde a la búsqueda (mDNS), consulta la hora durante el duplicado (NTP) y usa un canal de control del audio. AireCast siempre usa los mismos puertos (`Ports` en `AireCast.Core`), así que basta con abrir estos, y solo para la red local:

| Puerto | Para qué |
|---|---|
| 5353/udp | Respuestas a la búsqueda (mDNS) |
| 27450-27459/udp | Hora (NTP) y control del audio |

Cada uso toma el primer puerto libre del rango; si los diez estuvieran ocupados, usa uno aleatorio. En los comandos, cambia `192.168.1.0/24` por la red de tu casa.

**ufw** (Ubuntu, Mint):

```bash
sudo ufw allow from 192.168.1.0/24 to any port 5353 proto udp comment 'AireCast mDNS'
sudo ufw allow from 192.168.1.0/24 to any port 27450:27459 proto udp comment 'AireCast'
```

**firewalld** (Fedora). La zona `FedoraWorkstation` ya deja pasar los puertos 1025-65535, así que con ella solo falta el de mDNS; en zonas más cerradas hacen falta los dos:

```bash
sudo firewall-cmd --permanent --add-rich-rule='rule family=ipv4 source address=192.168.1.0/24 port port=5353 protocol=udp accept'
sudo firewall-cmd --permanent --add-rich-rule='rule family=ipv4 source address=192.168.1.0/24 port port=27450-27459 protocol=udp accept'
sudo firewall-cmd --reload
```

**Firewall de Windows** (PowerShell como administrador, con la red marcada como privada):

```powershell
New-NetFirewallRule -DisplayName "AireCast mDNS" -Direction Inbound -Protocol UDP -LocalPort 5353 -RemoteAddress LocalSubnet -Profile Private -Action Allow
New-NetFirewallRule -DisplayName "AireCast UDP" -Direction Inbound -Protocol UDP -LocalPort 27450-27459 -RemoteAddress LocalSubnet -Profile Private -Action Allow
```

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

## Codificación

Al compartir, AireCast prueba los codificadores por hardware de FFmpeg y usa el primero que funcione: VAAPI y NVENC en Linux; NVENC, AMF y Quick Sync en Windows. Si ninguno funciona, usa x264. El método elegido aparece en **Ajustes → Registro técnico**, y **Codificar con la GPU** obliga a usar x264.

En Linux, el FFmpeg incluido en los paquetes es estático y no accede a la GPU: hace falta un `ffmpeg` del sistema y el driver VAAPI. En Fedora con gráfica AMD, ese driver es `mesa-va-drivers-freeworld`, de RPM Fusion.

El hardware gasta menos CPU pero más red que x264: su bitrate medio es el 60 % del tope.
