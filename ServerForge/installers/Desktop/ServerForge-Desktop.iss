; Inno Setup Script — ServerForge Desktop (Native WPF .NET 10 Self-Contained win-x64)
; Produces: ServerForge-Desktop-Setup.exe
; SAFETY: Scopes CloseApplicationsFilter strictly to ServerForge.Desktop.exe so unrelated apps are never closed.
; UPGRADE: Preserves %LOCALAPPDATA%\ServerForge (SQLite metadata, DPAPI certs & local settings).

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
DefaultDirName={autopf}\ServerForge Desktop
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
SetupIconFile=..\Assets\serverforge.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=ServerForge Desktop — Native Windows Self-Hosted Deployment Control Center
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
AllowNoIcons=yes
SetupLogging=yes

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
; Preserve %LOCALAPPDATA%\ServerForge (SQLite metadata, DPAPI certs & paired server records) across upgrades and uninstalls
Type: dirifempty; Name: "{app}"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsWin64 then
  begin
    Log('Aborting ServerForge Desktop install: 64-bit Windows is required.');
    SuppressibleMsgBox('ServerForge Desktop requires 64-bit Windows 10 (2004+) or Windows 11.', mbCriticalError, MB_OK, IDOK);
    Result := False;
  end;
end;
