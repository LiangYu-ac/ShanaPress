#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef PayloadDir
  #define PayloadDir "..\artifacts\setup-payload"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif
#ifndef OutputBaseFilename
  #define OutputBaseFilename "ShanaPress-Setup-v1.0.0-win-x64"
#endif

[Setup]
AppId={{8E2C6B54-7D1F-4F3A-9C8B-2A4E5D6F7011}
AppName=ShanaPress
AppVersion={#MyAppVersion}
AppVerName=ShanaPress {#MyAppVersion}
AppPublisher=ShanaPress
AppPublisherURL=https://github.com/maruiyuan914-ai/ShanaPress
AppSupportURL=https://github.com/maruiyuan914-ai/ShanaPress/issues
AppUpdatesURL=https://github.com/maruiyuan914-ai/ShanaPress/releases
DefaultDirName={autopf}\ShanaPress
DefaultGroupName=ShanaPress
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile=..\src\ShanaPress.App\Assets\app.ico
UninstallDisplayIcon={app}\ShanaPress.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
#ifdef HasZhCN
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ShanaPress"; Filename: "{app}\ShanaPress.exe"
Name: "{autodesktop}\ShanaPress"; Filename: "{app}\ShanaPress.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ShanaPress.exe"; Description: "{cm:LaunchProgram,ShanaPress}"; Flags: nowait postinstall skipifsilent
