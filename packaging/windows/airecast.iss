; Instalador de Windows (Inno Setup). Lo compila packaging/windows/build.ps1, que define las rutas y la versión.
[Setup]
AppName=AireCast
AppVersion={#Version}
AppPublisher=maximiliano1401
AppPublisherURL=https://github.com/maximiliano1401/AireCast
DefaultDirName={autopf}\AireCast
DefaultGroupName=AireCast
DisableProgramGroupPage=yes
PrivilegesRequiredOverridesAllowed=dialog
OutputBaseFilename=AireCast-{#Version}-Setup
OutputDir={#Output}
SetupIconFile={#Assets}\logo.ico
UninstallDisplayIcon={app}\AireCast.App.exe
LicenseFile={#Root}\LICENSE
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\AireCast"; Filename: "{app}\AireCast.App.exe"
Name: "{autodesktop}\AireCast"; Filename: "{app}\AireCast.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\AireCast.App.exe"; Description: "Abrir AireCast"; Flags: nowait postinstall skipifsilent
