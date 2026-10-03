#ifndef AppVersion
#define AppVersion "0.1.0"
#endif

[Setup]
AppId={{8F2C1A55-6B1D-4B7E-9A30-5C4D2E7F1A11}
AppName=Jeet Screen Recorder
AppVersion={#AppVersion}
AppPublisher=Jeet Screen Recorder
DefaultDirName={autopf}\Jeet Screen Recorder
DefaultGroupName=Jeet Screen Recorder
OutputDir=..\dist
OutputBaseFilename=JeetScreenRecorder-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
UninstallDisplayIcon={app}\JeetScreenRecorder.exe

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Jeet Screen Recorder"; Filename: "{app}\JeetScreenRecorder.exe"
Name: "{autodesktop}\Jeet Screen Recorder"; Filename: "{app}\JeetScreenRecorder.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\JeetScreenRecorder.exe"; Description: "Launch Jeet Screen Recorder"; Flags: nowait postinstall skipifsilent
