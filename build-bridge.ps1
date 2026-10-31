# Prepara los recursos que electron-builder incluye en el .exe:
#   build/bridge/  -> bridge.exe (puente Python compilado con PyInstaller)
#   build/ffmpeg/  -> ffmpeg.exe
#   build/audiocap/ -> audiocap.exe (captura de audio por proceso, ver audiocap.cs)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$python = Join-Path $PSScriptRoot ".venv\Scripts\python.exe"
& $python -m pip install --quiet pyinstaller
if ($LASTEXITCODE) { throw "No se pudo instalar PyInstaller" }

Remove-Item build -Recurse -Force -ErrorAction SilentlyContinue
& $python -m PyInstaller bridge.py --name bridge --onedir --noconfirm --clean `
    --collect-all pyatv --collect-all miniaudio --collect-submodules zeroconf `
    --distpath build --workpath build\pyinstaller --specpath build\pyinstaller
if ($LASTEXITCODE) { throw "PyInstaller falló" }

$ffmpeg = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\Gyan.FFmpeg*\ffmpeg-*\bin\ffmpeg.exe" | Select-Object -First 1
if (-not $ffmpeg) { throw "No se encontró ffmpeg.exe (instálalo con: winget install Gyan.FFmpeg)" }
New-Item -ItemType Directory -Force build\ffmpeg | Out-Null
Copy-Item $ffmpeg.FullName build\ffmpeg\ffmpeg.exe

& (Join-Path $PSScriptRoot "build-audiocap.ps1")

Write-Host "Recursos listos en build\bridge, build\ffmpeg y build\audiocap"
