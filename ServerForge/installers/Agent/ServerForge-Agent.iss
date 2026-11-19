; Inno Setup Script — ServerForge Agent (Native .NET 10 Windows Service win-x64)
; Produces: ServerForge-Agent-Setup.exe
; SAFETY GUARANTEE: Never deletes, modifies, or restarts pre-existing IIS sites, AppPools, or SQL databases.

#define MyAppName "ServerForge Agent"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "ServerForge Systems"
#define MyAppExeName "ServerForge.Agent.exe"
#define ServiceName "ServerForgeAgent"
#define PublishDir "..\..\artifacts\publish\agent-win-x64"

[Setup]
AppId={{9B4E2D18-7F3A-4C61-A82E-1F4A9C7E0002}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\ServerForge\Agent
DefaultGroupName=ServerForge Agent
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
OutputDir=..\..\artifacts\installers
OutputBaseFilename=ServerForge-Agent-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=ServerForge Agent — Autonomous Windows Server Deployment Service
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Dirs]
Name: "{commonappdata}\ServerForge\Agent"; Permissions: admins-full system-full
Name: "{commonappdata}\ServerForge\Agent\logs"; Permissions: admins-full system-full
Name: "{commonappdata}\ServerForge\tools"; Permissions: admins-full system-full
Name: "C:\ServerForgeApps"; Permissions: admins-full system-full

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "appsettings.json"
; Preserve existing appsettings.json on upgrades
Source: "{#PublishDir}\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist

[Run]
; Stop previous agent service safely if upgrading (never touches unrelated services)
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; StatusMsg: "Checking existing ServerForge Agent service..."
; Register or update ServerForgeAgent Windows Service
Filename: "{sys}\sc.exe"; Parameters: "create {#ServiceName} binPath= ""\""{app}\{#MyAppExeName}\"""" start= auto DisplayName= ""ServerForge Agent"""; Flags: runhidden waituntilterminated; StatusMsg: "Registering ServerForge Agent Windows Service..."
Filename: "{sys}\sc.exe"; Parameters: "description {#ServiceName} ""ServerForge autonomous deployment, IIS management, and monitoring agent."""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure {#ServiceName} reset= 86400 actions= restart/5000/restart/15000/restart/30000"; Flags: runhidden waituntilterminated
; Start service and generate initial one-time pairing code
Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; Flags: runhidden waituntilterminated; StatusMsg: "Starting ServerForge Agent service..."
Filename: "{sys}\notepad.exe"; Parameters: """{commonappdata}\ServerForge\Agent\pairing-code.txt"""; Description: "View One-Time Pairing Code & Server Certificate Thumbprint"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Safe uninstall: stops/removes ONLY ServerForgeAgent; NEVER deletes C:\ServerForgeApps or hosted IIS websites
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopAgent"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteAgent"

[Code]
function InitializeSetup(): Boolean;
var
  IisExists: Boolean;
begin
  Result := True;
  IisExists := FileExists(ExpandConstant('{sys}\inetsrv\w3wp.exe'));
  if not IisExists then
  begin
    if MsgBox('Microsoft IIS (w3wp.exe) was not detected on this machine. ' +
              'ServerForge Agent can still be installed, but IIS hosting features require the Web-Server role. Continue?',
              mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
    end;
  end;
end;
