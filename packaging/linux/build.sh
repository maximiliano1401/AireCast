#!/usr/bin/env bash
# Genera los paquetes de Linux en dist/packages/: .deb, .rpm y AppImage. Incluyen la app y un FFmpeg
# estático con libx264 y x11grab. Necesita el SDK de .NET 10, curl, ar y rpmbuild (rpm-build en Fedora,
# rpm en Debian y Ubuntu). Lo demás (GStreamer, pactl…) viene del sistema: el .deb y el .rpm lo declaran.
#
# Con --no-appimage no genera el AppImage, que descarga y ejecuta appimagetool.
set -euo pipefail
cd "$(dirname "$0")/../.."

FFMPEG_VERSION=7.0.2
FFMPEG_SHA256=abda8d77ce8309141f83ab8edf0596834087c52467f6badf376a6a2a4c87cf67
APPIMAGETOOL_VERSION=1.9.0
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' core/Directory.Build.props)
OUT=$PWD/dist/packages
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$OUT"

# --- La app ------------------------------------------------------------------------------------
dotnet publish core/AireCast.App -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true \
    -p:PublishTrimmed=true -p:InvariantGlobalization=true -p:DebugType=none \
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "$WORK/app" -v q

# --- FFmpeg estático ---------------------------------------------------------------------------
# Las versiones nuevas están en releases/ y las anteriores pasan a old-releases/.
archive="ffmpeg-$FFMPEG_VERSION-amd64-static.tar.xz"
for dir in releases old-releases; do
    curl -fsSL -o "$WORK/$archive" "https://johnvansickle.com/ffmpeg/$dir/$archive" && break
done
echo "$FFMPEG_SHA256  $WORK/$archive" | sha256sum -c --quiet -
tar -xf "$WORK/$archive" -C "$WORK"
ffmpeg_dir="$WORK/ffmpeg-$FFMPEG_VERSION-amd64-static"

# --- Árbol común -------------------------------------------------------------------------------
root="$WORK/root"
install -Dm755 "$WORK/app/AireCast.App" "$root/opt/airecast/AireCast.App"
install -Dm755 "$ffmpeg_dir/ffmpeg" "$root/opt/airecast/ffmpeg"
install -Dm644 LICENSE "$root/opt/airecast/LICENSE"
install -Dm644 THIRD_PARTY_NOTICES.md "$root/opt/airecast/THIRD_PARTY_NOTICES.md"
install -Dm644 "$ffmpeg_dir/GPLv3.txt" "$root/opt/airecast/LICENSE-FFmpeg-GPLv3.txt"
install -Dm644 "$ffmpeg_dir/readme.txt" "$root/opt/airecast/README-FFmpeg.txt"
install -Dm644 packaging/linux/airecast.desktop "$root/usr/share/applications/airecast.desktop"
install -Dm644 assets/logo-256.png "$root/usr/share/icons/hicolor/256x256/apps/airecast.png"
install -Dm644 assets/logo-512.png "$root/usr/share/icons/hicolor/512x512/apps/airecast.png"
mkdir -p "$root/usr/bin"
ln -s /opt/airecast/AireCast.App "$root/usr/bin/airecast"

SUMMARY="Duplica tu pantalla en televisores AirPlay"

# --- .deb (ar con control y datos: no hace falta dpkg) -------------------------------------------
deb="$WORK/deb"
mkdir -p "$deb/control"
cat > "$deb/control/control" <<CONTROL
Package: airecast
Version: $VERSION
Architecture: amd64
Maintainer: maximiliano1401 <bmdp149@hotmail.com>
Depends: pulseaudio-utils, x11-xserver-utils, xdg-desktop-portal, gstreamer1.0-tools, gstreamer1.0-plugins-base, gstreamer1.0-pipewire, libice6, libsm6, libx11-6, libfontconfig1
Section: video
Priority: optional
Homepage: https://github.com/maximiliano1401/AireCast
Description: $SUMMARY
CONTROL
tar --owner=0 --group=0 -czf "$deb/control.tar.gz" -C "$deb/control" .
tar --owner=0 --group=0 -cJf "$deb/data.tar.xz" -C "$root" .
echo 2.0 > "$deb/debian-binary"
rm -f "$OUT/airecast_${VERSION}_amd64.deb"
(cd "$deb" && ar rc "$OUT/airecast_${VERSION}_amd64.deb" debian-binary control.tar.gz data.tar.xz)

# --- .rpm --------------------------------------------------------------------------------------
rpm="$WORK/rpm"
mkdir -p "$rpm/SPECS"
cat > "$rpm/SPECS/airecast.spec" <<SPEC
Name: airecast
Version: $VERSION
Release: 1
Summary: $SUMMARY
License: GPL-3.0-or-later
URL: https://github.com/maximiliano1401/AireCast
AutoReqProv: no
Requires: pulseaudio-utils, xrandr, xdg-desktop-portal, gstreamer1, gstreamer1-plugins-base, pipewire-gstreamer, libICE, libSM, libX11, fontconfig
%global debug_package %{nil}
%global __strip /bin/true
%global __brp_strip /bin/true

%description
$SUMMARY.

%install
cp -a "$root/." %{buildroot}/

%files
/opt/airecast
/usr/bin/airecast
/usr/share/applications/airecast.desktop
/usr/share/icons/hicolor/256x256/apps/airecast.png
/usr/share/icons/hicolor/512x512/apps/airecast.png
SPEC
rpmbuild -bb --quiet --target x86_64 --define "_topdir $rpm" --define "_rpmdir $rpm/out" \
    --define "_build_name_fmt %%{NAME}-%%{VERSION}.%%{ARCH}.rpm" "$rpm/SPECS/airecast.spec"
cp "$rpm/out/airecast-$VERSION.x86_64.rpm" "$OUT/"

# --- AppImage ----------------------------------------------------------------------------------
if [ "${1:-}" != "--no-appimage" ]; then
    appdir="$WORK/AireCast.AppDir"
    mkdir -p "$appdir/usr/bin"
    cp -a "$root/opt/airecast/." "$appdir/usr/bin/"
    cp packaging/linux/airecast.desktop "$appdir/airecast.desktop"
    cp assets/logo-256.png "$appdir/airecast.png"
    printf '#!/bin/sh\nexec "$(dirname "$(readlink -f "$0")")/usr/bin/AireCast.App" "$@"\n' > "$appdir/AppRun"
    chmod +x "$appdir/AppRun"
    curl -fsSL -o "$WORK/appimagetool" \
        "https://github.com/AppImage/appimagetool/releases/download/$APPIMAGETOOL_VERSION/appimagetool-x86_64.AppImage"
    chmod +x "$WORK/appimagetool"
    ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$WORK/appimagetool" --no-appstream "$appdir" "$OUT/AireCast-$VERSION-x86_64.AppImage"
fi

ls -la "$OUT"
