# Compila audiocap.cs (captura de audio por proceso) en build/audiocap.exe
# con el compilador de C# de .NET Framework que ya viene en Windows.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "No se encontró csc.exe de .NET Framework 4 en $csc" }

New-Item -ItemType Directory -Force build | Out-Null
& $csc -nologo -optimize -out:build\audiocap.exe audiocap.cs
if ($LASTEXITCODE) { throw "No se pudo compilar audiocap.cs" }

Write-Host "audiocap listo en build"
