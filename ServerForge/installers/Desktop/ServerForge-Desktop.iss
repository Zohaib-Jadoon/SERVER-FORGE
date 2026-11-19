; Inno Setup Script — ServerForge Desktop (Native WPF .NET 10 Self-Contained win-x64)
; Produces: ServerForge-Desktop-Setup.exe

#define MyAppName "ServerForge Desktop"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "ServerForge Systems"
#define MyAppExeName "ServerForge.Desktop.exe"
#define PublishDir "..\..\artifacts\publish\desktop-win-x64"

[Setup]
AppId={{9B4E2D18-7F3A-4C61-A82E-1F4A9C7E0001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\ServerForge Desktop
DefaultGroupName=ServerForge
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\..\artifacts\installers
OutputBaseFilename=ServerForge-Desktop-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=ServerForge Desktop — Native Windows Self-Hosted Deployment Control Center
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Preserve %LOCALAPPDATA%\ServerForge (SQLite metadata, DPAPI certs & local settings) across upgrades and uninstalls
Type: dirifempty; Name: "{app}"
