#!/usr/bin/env bash
# Genera el APK firmado en dist/packages/. Necesita el SDK de .NET 10 con la carga de trabajo «android»,
# JAVA_HOME (JDK 17), ANDROID_HOME y la clave de firma: ANDROID_KEYSTORE (ruta) y ANDROID_KEY_PASSWORD.
#
# Esa clave es la identidad de la app: con otra, Android no deja actualizar y hay que desinstalar.
set -euo pipefail
cd "$(dirname "$0")/../.."

: "${ANDROID_KEYSTORE:?Falta ANDROID_KEYSTORE, la ruta de la clave de firma}"
: "${ANDROID_KEY_PASSWORD:?Falta ANDROID_KEY_PASSWORD}"
: "${ANDROID_HOME:?Falta ANDROID_HOME, la carpeta del SDK de Android}"
: "${JAVA_HOME:?Falta JAVA_HOME, la carpeta del JDK 17}"
export ANDROID_KEY_PASSWORD

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' core/Directory.Build.props)
OUT=$PWD/dist/packages
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$OUT"

dotnet publish core/AireCast.Android -c Release -o "$WORK" -v q \
    -p:AndroidSdkDirectory="$ANDROID_HOME" -p:JavaSdkDirectory="$JAVA_HOME" \
    -p:AndroidKeyStore=true -p:AndroidSigningKeyStore="$ANDROID_KEYSTORE" -p:AndroidSigningKeyAlias=airecast \
    -p:AndroidSigningStorePass=env:ANDROID_KEY_PASSWORD -p:AndroidSigningKeyPass=env:ANDROID_KEY_PASSWORD

cp "$WORK"/*-Signed.apk "$OUT/AireCast-$VERSION.apk"
ls -la "$OUT/AireCast-$VERSION.apk"
