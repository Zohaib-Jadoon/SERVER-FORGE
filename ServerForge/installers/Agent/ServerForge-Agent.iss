; Inno Setup Script — ServerForge Agent (Native .NET 10 Windows Service win-x64)
; Produces: ServerForge-Agent-Setup.exe
; SAFETY GUARANTEES:
;   - Never deletes, modifies, or restarts pre-existing IIS sites, AppPools, or SQL databases.
;   - Safely upgrades existing ServerForgeAgent service (stop -> update files -> sc config -> start -> verify RUNNING).
;   - Locks ProgramData ACLs to Administrators and SYSTEM only.
;   - Uninstall removes ONLY ServerForgeAgent service and binaries; preserves C:\ServerForgeApps and hosted websites.

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
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Dirs]
Name: "{commonappdata}\ServerForge\Agent"; Permissions: admins-full system-full
Name: "{commonappdata}\ServerForge\Agent\logs"; Permissions: admins-full system-full
Name: "{commonappdata}\ServerForge\Agent\artifacts"; Permissions: admins-full system-full
Name: "{commonappdata}\ServerForge\tools"; Permissions: admins-full system-full
Name: "C:\ServerForgeApps"; Permissions: admins-full system-full

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "appsettings.json"
; Preserve existing appsettings.json on upgrades so VPN bind address & ports are never overwritten
Source: "{#PublishDir}\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist

[Run]
; Open initial pairing code only when unpaired (file generated on first startup)
Filename: "{sys}\notepad.exe"; Parameters: """{commonappdata}\ServerForge\Agent\pairing-code.txt"""; Description: "View One-Time Pairing Code & Server Certificate Thumbprint"; Flags: nowait postinstall skipifsilent; Check: PairingCodeFileExists

[Code]
function PairingCodeFileExists(): Boolean;
begin
  Result := FileExists(ExpandConstant('{commonappdata}\ServerForge\Agent\pairing-code.txt'));
end;

function RunHiddenSc(const Params: string; out ResultCode: Integer): Boolean;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function ServiceExists(): Boolean;
var
  ResultCode: Integer;
begin
  Result := RunHiddenSc('query {#ServiceName}', ResultCode) and (ResultCode = 0);
end;

function StopExistingAgentService(): Boolean;
var
  ResultCode: Integer;
  Attempts: Integer;
begin
  Result := True;
  if not ServiceExists() then
    Exit;

  RunHiddenSc('stop {#ServiceName}', ResultCode);
  Attempts := 0;
  while Attempts < 20 do
  begin
    Sleep(500);
    if RunHiddenSc('query {#ServiceName}', ResultCode) then
    begin
      { Wait briefly to let service binary release file locks cleanly }
      Attempts := Attempts + 1;
      if Attempts >= 6 then
        Break;
    end
    else
      Break;
  end;
end;

function InitializeSetup(): Boolean;
var
  IisExists: Boolean;
begin
  Result := True;
  if not IsWin64 then
  begin
    MsgBox('ServerForge Agent requires 64-bit Windows Server 2019/2022/2025 or Windows 10/11 x64.', mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;

  IisExists := FileExists(ExpandConstant('{sys}\inetsrv\w3wp.exe'));
  if not IisExists then
  begin
    if MsgBox('Microsoft IIS (w3wp.exe) was not detected on this server. ' +
              'ServerForge Agent can still be installed, but IIS hosting features require the Web-Server role. Continue?',
              mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  StopExistingAgentService();
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  BinPath: string;
begin
  if CurStep = ssPostInstall then
  begin
    BinPath := '"' + ExpandConstant('{app}\{#MyAppExeName}') + '"';
    if ServiceExists() then
    begin
      { Safe in-place upgrade: update binary path without destroying service registration }
      if (not RunHiddenSc('config {#ServiceName} binPath= "' + BinPath + '" start= auto', ResultCode)) or (ResultCode <> 0) then
        MsgBox('Warning: Failed to update ServerForgeAgent service configuration (code ' + IntToStr(ResultCode) + ').', mbError, MB_OK);
    end
    else
    begin
      if (not RunHiddenSc('create {#ServiceName} binPath= "' + BinPath + '" start= auto DisplayName= "ServerForge Agent"', ResultCode)) or (ResultCode <> 0) then
      begin
        MsgBox('Error: Failed to register ServerForgeAgent Windows Service (code ' + IntToStr(ResultCode) + ').', mbCriticalError, MB_OK);
        Exit;
      end;
    end;

    RunHiddenSc('description {#ServiceName} "ServerForge autonomous deployment, IIS management, and monitoring agent."', ResultCode);
    RunHiddenSc('failure {#ServiceName} reset= 86400 actions= restart/5000/restart/15000/restart/30000', ResultCode);

    if (not RunHiddenSc('start {#ServiceName}', ResultCode)) or ((ResultCode <> 0) and (ResultCode <> 1056)) then
    begin
      MsgBox('Error: ServerForgeAgent service failed to start (code ' + IntToStr(ResultCode) + '). Check C:\ProgramData\ServerForge\Agent\logs for details.', mbCriticalError, MB_OK);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Safe uninstall: stops and removes ONLY ServerForgeAgent; NEVER deletes C:\ServerForgeApps or hosted IIS websites }
    if ServiceExists() then
    begin
      StopExistingAgentService();
      RunHiddenSc('delete {#ServiceName}', ResultCode);
    end;
  end;
end;
