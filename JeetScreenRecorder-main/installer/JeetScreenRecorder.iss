#ifndef AppVersion
#define AppVersion "0.2.0"
#endif

[Setup]
AppId={{8F2C1A55-6B1D-4B7E-9A30-5C4D2E7F1A11}
AppName=Jeet Screen Recorder
AppVersion={#AppVersion}
AppPublisher=Amarjeet K Gupta
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
SetupIconFile=..\src\JeetScreenRecorder\Assets\JeetScreenRecorder.ico
WizardImageFile=wizard_large_164.bmp,wizard_large_246.bmp,wizard_large_328.bmp
WizardSmallImageFile=wizard_small_55.bmp,wizard_small_64.bmp,wizard_small_80.bmp,wizard_small_110.bmp
DisableProgramGroupPage=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Jeet Screen Recorder"; Filename: "{app}\JeetScreenRecorder.exe"; IconFilename: "{app}\JeetScreenRecorder.exe"
Name: "{autodesktop}\Jeet Screen Recorder"; Filename: "{app}\JeetScreenRecorder.exe"; IconFilename: "{app}\JeetScreenRecorder.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\JeetScreenRecorder.exe"; Description: "Launch Jeet Screen Recorder"; Flags: nowait postinstall skipifsilent
