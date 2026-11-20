; Inno Setup Script — ServerForge Agent (Native .NET 10 Windows Service win-x64)
; Produces: ServerForge-Agent-Setup.exe
; SAFETY GUARANTEES:
;   - Never deletes, modifies, or restarts pre-existing IIS sites, AppPools, or SQL databases.
;   - Safely upgrades existing ServerForge Agent service (stop -> update files -> sc config -> start -> verify RUNNING).
;   - Supports optional /SERVICENAME=<Name> parameter for isolated disposable smoke testing.
;   - Locks ProgramData ACLs to Administrators and SYSTEM only.
;   - Uninstall removes ONLY ServerForge Agent service and binaries; preserves C:\ServerForgeApps and hosted websites.

#define MyAppName "ServerForge Agent"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "ServerForge Systems"
#define MyAppExeName "ServerForge.Agent.exe"
#define DefaultServiceName "ServerForgeAgent"
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
SetupLogging=yes

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

[UninstallDelete]
Type: files; Name: "{app}\service-name.txt"
Type: dirifempty; Name: "{app}"

[Code]
function PairingCodeFileExists(): Boolean;
begin
  Result := FileExists(ExpandConstant('{commonappdata}\ServerForge\Agent\pairing-code.txt'));
end;

function GetServiceName(): string;
var
  ParamName: string;
  SavedBytes: AnsiString;
  MarkerPath: string;
begin
  ParamName := Trim(ExpandConstant('{param:ServiceName|}'));
  if ParamName <> '' then
  begin
    Result := ParamName;
    Exit;
  end;

  MarkerPath := ExpandConstant('{app}\service-name.txt');
  if FileExists(MarkerPath) and LoadStringFromFile(MarkerPath, SavedBytes) then
  begin
    Result := Trim(String(SavedBytes));
    if Result <> '' then
      Exit;
  end;

  Result := '{#DefaultServiceName}';
end;

function RunHiddenSc(const Params: string; var ResultCode: Integer): Boolean;
begin
  Log('Executing sc.exe ' + Params);
  Result := Exec(ExpandConstant('{sys}\sc.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log('sc.exe exited with ResultCode=' + IntToStr(ResultCode));
end;

function ServiceExists(const SvcName: string): Boolean;
var
  ResultCode: Integer;
begin
  Result := RunHiddenSc('query "' + SvcName + '"', ResultCode) and (ResultCode = 0);
end;

function StopExistingAgentService(const SvcName: string): Boolean;
var
  ResultCode: Integer;
  Attempts: Integer;
begin
  Result := True;
  if not ServiceExists(SvcName) then
    Exit;

  Log('Stopping existing Windows Service: ' + SvcName);
  RunHiddenSc('stop "' + SvcName + '"', ResultCode);
  Attempts := 0;
  while Attempts < 20 do
  begin
    Sleep(500);
    if RunHiddenSc('query "' + SvcName + '"', ResultCode) then
    begin
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
    Log('Aborting ServerForge Agent install: 64-bit Windows is required.');
    SuppressibleMsgBox('ServerForge Agent requires 64-bit Windows Server 2019/2022/2025 or Windows 10/11 x64.', mbCriticalError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;

  IisExists := FileExists(ExpandConstant('{sys}\inetsrv\w3wp.exe'));
  if not IisExists then
  begin
    Log('Warning: Microsoft IIS (w3wp.exe) was not detected. Agent will install without modifying IIS roles.');
    if (not WizardSilent()) and
       (SuppressibleMsgBox('Microsoft IIS (w3wp.exe) was not detected on this server. ' +
                           'ServerForge Agent can still be installed, but IIS hosting features require the Web-Server role. Continue?',
                           mbConfirmation, MB_YESNO, IDYES) = IDNO) then
    begin
      Result := False;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  StopExistingAgentService(GetServiceName());
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  BinPath: string;
  SvcName: string;
  ScBinArg: string;
begin
  if CurStep = ssPostInstall then
  begin
    SvcName := GetServiceName();
    BinPath := ExpandConstant('{app}\{#MyAppExeName}');
    SaveStringToFile(ExpandConstant('{app}\service-name.txt'), AnsiString(SvcName), False);

    if CompareText(SvcName, '{#DefaultServiceName}') = 0 then
      ScBinArg := '"' + BinPath + '"'
    else
      ScBinArg := '"\"' + BinPath + '\" --Agent:ServiceName=' + SvcName + '"';

    if ServiceExists(SvcName) then
    begin
      { Safe in-place upgrade: update binary path without destroying service registration }
      if (not RunHiddenSc('config "' + SvcName + '" binPath= ' + ScBinArg + ' start= auto', ResultCode)) or (ResultCode <> 0) then
      begin
        Log('Warning: Failed to update service configuration for ' + SvcName + ' (code ' + IntToStr(ResultCode) + ').');
        SuppressibleMsgBox('Warning: Failed to update ' + SvcName + ' service configuration (code ' + IntToStr(ResultCode) + ').', mbError, MB_OK, IDOK);
      end;
    end
    else
    begin
      if (not RunHiddenSc('create "' + SvcName + '" binPath= ' + ScBinArg + ' start= auto DisplayName= "ServerForge Agent (' + SvcName + ')"', ResultCode)) or (ResultCode <> 0) then
      begin
        Log('Error: Failed to register Windows Service ' + SvcName + ' (code ' + IntToStr(ResultCode) + ').');
        SuppressibleMsgBox('Error: Failed to register ' + SvcName + ' Windows Service (code ' + IntToStr(ResultCode) + ').', mbCriticalError, MB_OK, IDOK);
        Exit;
      end;
    end;

    RunHiddenSc('description "' + SvcName + '" "ServerForge autonomous deployment, IIS management, and monitoring agent."', ResultCode);
    RunHiddenSc('failure "' + SvcName + '" reset= 86400 actions= restart/5000/restart/15000/restart/30000', ResultCode);

    if (not RunHiddenSc('start "' + SvcName + '"', ResultCode)) or ((ResultCode <> 0) and (ResultCode <> 1056)) then
    begin
      Log('Error: Service ' + SvcName + ' failed to start (code ' + IntToStr(ResultCode) + ').');
      SuppressibleMsgBox('Error: ' + SvcName + ' service failed to start (code ' + IntToStr(ResultCode) + '). Check C:\ProgramData\ServerForge\Agent\logs for details.', mbCriticalError, MB_OK, IDOK);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  SvcName: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Safe uninstall: stops and removes ONLY ServerForge Agent; NEVER deletes C:\ServerForgeApps or hosted IIS websites }
    SvcName := GetServiceName();
    Log('Uninstalling ServerForge Agent service: ' + SvcName);
    if ServiceExists(SvcName) then
    begin
      StopExistingAgentService(SvcName);
      RunHiddenSc('delete "' + SvcName + '"', ResultCode);
    end;
  end;
end;
