#define AppName "SyncWave Audio"
#define AppVersion "1.0.0"
#define Publisher "SyncWave Audio"
#define SourceDir "..\artifacts\publish\SyncWaveAudio"

[Setup]
AppId={{7A64687B-16F4-453B-9F80-0F97FD685E6E}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={autopf}\SyncWave Audio
DefaultGroupName=SyncWave Audio
OutputDir=..\artifacts\installer
OutputBaseFilename=SyncWaveAudioSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SyncWave Audio"; Filename: "{app}\SyncWaveAudio.exe"
Name: "{autodesktop}\SyncWave Audio"; Filename: "{app}\SyncWaveAudio.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Run]
Filename: "{app}\SyncWaveAudio.exe"; Description: "Launch SyncWave Audio"; Flags: nowait postinstall skipifsilent
