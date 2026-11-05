#!/usr/bin/env bash
# Compila la app y la consola autónomas (no necesitan .NET instalado) en dist/<app|cli>-<sistema>/.
# Recortadas, para que pesen ~22 MB. Hace falta el SDK de .NET 10.
set -euo pipefail
cd "$(dirname "$0")"

common=(-c Release --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true
    -p:InvariantGlobalization=true -p:DebugType=none -v q)
rm -rf dist
for runtime in linux-x64 win-x64; do
    dotnet publish AireCast.App -r "$runtime" "${common[@]}" -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true -o "dist/app-$runtime"
    dotnet publish AireCast.Cli -r "$runtime" "${common[@]}" -o "dist/cli-$runtime"
    rm -f "dist/app-$runtime"/*.pdb "dist/cli-$runtime"/*.pdb
done
du -sh dist/*
