# Genera el instalador de Windows en dist\packages\. Incluye la app, audiocap.exe y un FFmpeg GPL de
# gyan.dev. Necesita el SDK de .NET 10 e Inno Setup 6 (winget install JRSoftware.InnoSetup).
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $root

[xml]$props = Get-Content core\Directory.Build.props
$version = $props.Project.PropertyGroup.Version
$stage = Join-Path $root "dist\windows"
$output = Join-Path $root "dist\packages"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage, $output | Out-Null

# La app; AudioCap.targets compila audiocap.exe y lo deja junto a ella.
dotnet publish core\AireCast.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true `
    -p:PublishTrimmed=true -p:InvariantGlobalization=true -p:DebugType=none `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $stage
if ($LASTEXITCODE) { throw "No se pudo compilar la app" }
Remove-Item (Join-Path $stage "*.pdb") -ErrorAction SilentlyContinue
if (-not (Test-Path (Join-Path $stage "audiocap.exe"))) { throw "Falta audiocap.exe: sin él la TV recibe silencio" }

# FFmpeg GPL con libx264, con su licencia.
$zip = Join-Path $env:TEMP "ffmpeg-release-essentials.zip"
$unzipped = Join-Path $env:TEMP "airecast-ffmpeg"
Invoke-WebRequest "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" -OutFile $zip
Remove-Item $unzipped -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive $zip $unzipped
$ffmpeg = Get-ChildItem $unzipped -Recurse -Filter ffmpeg.exe | Select-Object -First 1
Copy-Item $ffmpeg.FullName $stage
Copy-Item (Join-Path $ffmpeg.Directory.Parent.FullName "LICENSE") (Join-Path $stage "LICENSE-FFmpeg.txt")
Copy-Item LICENSE, THIRD_PARTY_NOTICES.md $stage

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "No se encontró Inno Setup 6 (winget install JRSoftware.InnoSetup)" }
& $iscc "/DVersion=$version" "/DSource=$stage" "/DOutput=$output" "/DAssets=$root\assets" "/DRoot=$root" packaging\windows\airecast.iss
if ($LASTEXITCODE) { throw "Inno Setup falló" }
