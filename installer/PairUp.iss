#define MyAppName "PairUp"
#ifndef MyAppVersion
  #define MyAppVersion "0.2.0"
#endif
#define MyAppPublisher "Jamim Mehdi"
#define MyAppURL "https://github.com/jamimmehdi/pair-up-audio"
#define MyAppExeName "PairUp.App.exe"

[Setup]
AppId={{B7B6E7C1-9F1E-4B7D-9B7B-6A0B2E7C1A11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\PairUp
DefaultGroupName=PairUp
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=PairUp-Setup-{#MyAppVersion}
SetupIconFile=..\src\PairUp.App\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableWelcomePage=no
DisableDirPage=no

; Same AppId + DefaultDirName every version, so a newer Setup.exe run over an older
; install (including our own silent auto-update flow) just upgrades in place.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\src\PairUp.App\bin\Release\net8.0-windows\win-x64\publish\PairUp.App.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\PairUp"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\PairUp"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; No "skipifsilent" — this also fires during our own silent auto-update download+install
; flow (installer run with /VERYSILENT), so the app relaunches automatically post-update.
Filename: "{app}\{#MyAppExeName}"; Description: "Launch PairUp"; Flags: nowait postinstall

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/IM {#MyAppExeName} /F"; Flags: runhidden skipifdoesntexist; RunOnceId: "KillPairUp"
