; VIZEN — Inno Setup 6 script (no signing, no updater). VIZEN is free software under GPL-3.0.
; Build with scripts\build-installer.ps1, which publishes the app and passes the version and publish folder.
;   ISCC.exe /DMyAppVersion=0.3.0 /DPublishDir=<folder> installer\VIZEN.iss

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

#define MyAppName "VIZEN"
#define MyAppExeName "VIZEN.App.exe"

[Setup]
; Fixed AppId: identifies VIZEN for upgrades and its single Installed Apps entry. Never change it.
AppId={{8F3B2C71-6E4A-4D7B-9B1E-5A0C2E7D4F19}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} Setup
AppPublisher=kalinvisuals
AppPublisherURL=https://www.vizen.ro
AppSupportURL=https://www.vizen.ro
AppContact=hi@vizen.ro
AppCopyright=Copyright (C) 2026 kalinvisuals
VersionInfoCompany=kalinvisuals
VersionInfoCopyright=Copyright (C) 2026 kalinvisuals

; Wizard pages: welcome, destination, Start Menu folder, tasks, ready, progress, finish.
WizardStyle=modern
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=no
DisableReadyPage=no
DisableFinishedPage=no
AllowNoIcons=yes
WizardImageFile=assets\wizard-large-100.bmp,assets\wizard-large-150.bmp,assets\wizard-large-200.bmp
WizardSmallImageFile=assets\wizard-small-100.bmp,assets\wizard-small-150.bmp,assets\wizard-small-200.bmp
SetupIconFile=..\VIZEN.App\Assets\vizen.ico

DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UsePreviousAppDir=yes
UsePreviousGroup=yes

; 64-bit only, installs into C:\Program Files (needs administrator rights).
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.19041

; Close a running installed copy (Restart Manager) when reinstalling or upgrading.
CloseApplications=yes
RestartApplications=no

; Add/Remove Programs (Control Panel and Settings > Apps > Installed apps).
Uninstallable=yes
CreateUninstallRegKey=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

OutputDir=..\artifacts\installer
OutputBaseFilename=VIZEN-Setup-x64
Compression=lzma2/max
SolidCompression=yes
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole self-contained publish folder (runtime, WPF, WebView2 loader, NAudio, assets).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Started as the signed-in user, not elevated, so settings go to that user's profile.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; Update started from VIZEN (Settings > Updates, /SILENT /RELAUNCH=1): start VIZEN again when done.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser; Check: RelaunchAfterUpdate

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

{ True for a silent update started by VIZEN itself, which closed and asked to be started again. }
function RelaunchAfterUpdate(): Boolean;
begin
  Result := WizardSilent() and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

{ Runs a PowerShell command hidden and returns its exit code (-1 if it could not start). }
function RunPowerShell(const Command: String): Integer;
var
  Code: Integer;
begin
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Command + '"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := Code
  else
    Result := -1;
end;

{ True when a VIZEN process started from the installed folder is running.
  Only the installed copy counts: a development build elsewhere is never touched. }
function InstalledVizenRunning(): Boolean;
begin
  Result := RunPowerShell(
    '$d = ''' + ExpandConstant('{app}') + '\''; ' +
    'if (Get-Process VIZEN.App -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($d, ''OrdinalIgnoreCase'') }) { exit 1 } else { exit 0 }') = 1;
end;

procedure StopInstalledVizen();
begin
  RunPowerShell(
    '$d = ''' + ExpandConstant('{app}') + '\''; ' +
    'Get-Process VIZEN.App -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($d, ''OrdinalIgnoreCase'') } | ' +
    'ForEach-Object { $_.CloseMainWindow() | Out-Null; if (-not $_.WaitForExit(4000)) { $_.Kill() } }');
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if InstalledVizenRunning() then
  begin
    if UninstallSilent() or
      (MsgBox('VIZEN is running. It must be closed before it can be uninstalled.' + #13#10#13#10 +
              'Close VIZEN now and continue?', mbConfirmation, MB_YESNO) = IDYES) then
    begin
      StopInstalledVizen();
      Sleep(800);
      if InstalledVizenRunning() then
      begin
        MsgBox('VIZEN could not be closed. Close it manually and run the uninstaller again.', mbError, MB_OK);
        Result := False;
      end;
    end
    else
      Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { "Launch at sign-in" (set from VIZEN's Settings) would point to a removed file: drop it, but only
      when it points into this installation. }
    if RegQueryStringValue(HKCU, RunKey, 'VIZEN', Value) and
       (Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Value)) > 0) then
      RegDeleteValue(HKCU, RunKey, 'VIZEN');
  end;

  if (CurUninstallStep = usPostUninstall) and not UninstallSilent() then
    MsgBox('VIZEN was removed from this PC.' + #13#10#13#10 +
           'Your settings, profiles and media library were kept in:' + #13#10 +
           ExpandConstant('{localappdata}') + '\VIZEN' + #13#10#13#10 +
           'Delete that folder yourself if you no longer need them.', mbInformation, MB_OK);
end;
