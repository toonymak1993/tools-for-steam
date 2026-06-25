#define MyAppName "Tools for Steam"
#define MyAppShortName "TFS"
#define MyAppVersion "0.3.5"
#define MyAppPublisher "GCM - Gaming Console Mode"
#define MyAppExeName "ToolsForSteam.exe"
#define MyAppId "{{9A9F0B7E-4C79-4C7D-8E4B-0E0D766E0B72}"
#define PayloadDir "..\dist\portable"
#ifndef VariantOutputBaseFilename
  #define VariantOutputBaseFilename "ToolsForSteamSetup"
#endif
#ifndef VariantWizardStyle
  #define VariantWizardStyle "modern"
#endif
#ifndef VariantCustomPages
  #define VariantCustomPages "1"
#endif
#ifndef VariantFpsHelperPrep
  #define VariantFpsHelperPrep "1"
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/toonymak1993/tools-for-steam
AppSupportURL=https://github.com/toonymak1993/tools-for-steam/issues
AppUpdatesURL=https://github.com/toonymak1993/tools-for-steam/releases
DefaultDirName={localappdata}\Programs\ToolsForSteam
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE.txt
OutputDir=..\dist\installer
OutputBaseFilename={#VariantOutputBaseFilename}
Compression=lzma2/max
SolidCompression=yes
WizardStyle={#VariantWizardStyle}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SetupMutex=ToolsForSteamSetup
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion restartreplace
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
Type: files; Name: "{app}\SteamLoader.exe"
Type: files; Name: "{app}\SteamLoader.dll"
Type: files; Name: "{app}\SteamLoader.pdb"
Type: files; Name: "{app}\SteamLoader.deps.json"
Type: files; Name: "{app}\SteamLoader.runtimeconfig.json"
Type: files; Name: "{app}\install-toolsforsteam.ps1"
Type: files; Name: "{app}\uninstall-toolsforsteam.ps1"

[Icons]
Name: "{group}\Tools for Steam"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--manager"; WorkingDir: "{app}"
Name: "{group}\Start Tools for Steam"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--tray"; WorkingDir: "{app}"
Name: "{group}\Uninstall Tools for Steam"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Tools for Steam"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--manager"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--set-startup-mode=shell"; Flags: runhidden waituntilterminated runasoriginaluser skipifdoesntexist; Check: IsShellMode
Filename: "{app}\{#MyAppExeName}"; Parameters: "--set-startup-mode=tray"; Flags: runhidden waituntilterminated runasoriginaluser skipifdoesntexist; Check: IsTrayMode
Filename: "{app}\{#MyAppExeName}"; Parameters: "--tray"; Flags: nowait runasoriginaluser skipifnotsilent skipifdoesntexist; Check: IsShellMode
Filename: "{app}\{#MyAppExeName}"; Parameters: "--tray"; Flags: nowait runasoriginaluser skipifnotsilent skipifdoesntexist; Check: IsTrayMode

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM ToolsForSteam.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "CloseToolsForSteam"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM SteamLoader.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "CloseLegacySteamLoader"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""\ToolsForSteam\FpsHelper"" /F"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveFpsHelperTask"

[Code]
var
  SystemCheckPage: TWizardPage;
  StartupModePage: TWizardPage;
  GuidePage: TWizardPage;
  SystemCheckSummaryLabel: TNewStaticText;
  SystemCheckWindowsLabel: TNewStaticText;
  SystemCheckSteamLabel: TNewStaticText;
  SystemCheckInstallLabel: TNewStaticText;
  SystemCheckHelperLabel: TNewStaticText;
  SystemCheckRollbackLabel: TNewStaticText;
  StartupModeSummaryLabel: TNewStaticText;
  ShellModeRadio: TNewRadioButton;
  ShellModeDescriptionLabel: TNewStaticText;
  TrayModeRadio: TNewRadioButton;
  TrayModeDescriptionLabel: TNewStaticText;
  GuideSummaryLabel: TNewStaticText;
  GuideShellLabel: TNewStaticText;
  GuideSelectedModeLabel: TNewStaticText;
  LaunchSettingsAfterSetup: Boolean;
  PostInstallSettingsArguments: string;
  ExistingInstallPath: string;
  SteamInstallPath: string;
  CreatedBackupPath: string;

function GetRegistryStringValue(RootKey: Integer; SubKey: string; ValueName: string): string;
begin
  if not RegQueryStringValue(RootKey, SubKey, ValueName, Result) then
  begin
    Result := '';
  end;
end;

function EscapePowerShellSingleQuoted(Value: string): string;
begin
  Result := Value;
  StringChangeEx(Result, '''', '''''', True);
end;

procedure AppendLine(var Value: string; Line: string);
begin
  Value := Value + Line + #13#10;
end;

procedure UpdateSetupStatus(Message: string);
begin
  Log(Message);
  if WizardForm <> nil then
  begin
    WizardForm.StatusLabel.Caption := Message;
    WizardForm.Repaint;
  end;
end;

function GetExistingInstallLocation: string;
begin
  Result := GetRegistryStringValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppId}_is1',
    'InstallLocation');
  if (Result <> '') and DirExists(Result) then
  begin
    Exit;
  end;

  Result := GetRegistryStringValue(
    HKLM,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppId}_is1',
    'InstallLocation');
  if (Result <> '') and DirExists(Result) then
  begin
    Exit;
  end;

  Result := ExpandConstant('{localappdata}\Programs\ToolsForSteam');
  if not DirExists(Result) then
  begin
    Result := '';
  end;
end;

function GetSteamInstallLocation: string;
var
  Candidate: string;
begin
  Result := GetRegistryStringValue(HKCU, 'Software\Valve\Steam', 'SteamExe');
  if (Result <> '') and FileExists(Result) then
  begin
    Result := ExtractFileDir(Result);
    Exit;
  end;

  Result := GetRegistryStringValue(HKCU, 'Software\Valve\Steam', 'SteamPath');
  if (Result <> '') and DirExists(Result) then
  begin
    Exit;
  end;

  Result := GetRegistryStringValue(HKLM64, 'SOFTWARE\WOW6432Node\Valve\Steam', 'InstallPath');
  if (Result <> '') and DirExists(Result) then
  begin
    Exit;
  end;

  Result := GetRegistryStringValue(HKLM64, 'SOFTWARE\Valve\Steam', 'InstallPath');
  if (Result <> '') and DirExists(Result) then
  begin
    Exit;
  end;

  Candidate := ExpandConstant('{commonpf32}\Steam');
  if DirExists(Candidate) then
  begin
    Result := Candidate;
    Exit;
  end;

  Candidate := ExpandConstant('{commonpf}\Steam');
  if DirExists(Candidate) then
  begin
    Result := Candidate;
    Exit;
  end;

  Result := '';
end;

function GetResolvedInstallRootForChecks: string;
begin
  Result := ExistingInstallPath;
  if Result <> '' then
  begin
    Exit;
  end;

  Result := GetExistingInstallLocation;
  if Result <> '' then
  begin
    Exit;
  end;

  if WizardForm <> nil then
  begin
    Result := WizardDirValue;
  end
  else
  begin
    Result := '';
  end;
end;

function GetWindowsBuildNumber: Integer;
var
  BuildText: string;
begin
  BuildText := GetRegistryStringValue(HKLM64, 'SOFTWARE\Microsoft\Windows NT\CurrentVersion', 'CurrentBuildNumber');
  Result := StrToIntDef(BuildText, 0);
end;

function GetWindowsBuildRevision: Integer;
var
  Revision: Cardinal;
begin
  if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\Windows NT\CurrentVersion', 'UBR', Revision) then
  begin
    Result := Integer(Revision);
  end
  else
  begin
    Result := 0;
  end;
end;

procedure CloseProcess(ImageName: string);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM "' + ImageName + '" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure RequestToolsForSteamShutdown;
var
  ResultCode: Integer;
begin
  Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command "try { Invoke-WebRequest -UseBasicParsing -Method Post -Uri ''http://127.0.0.1:47652/api/control/shutdown'' -TimeoutSec 2 | Out-Null } catch {}"',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

procedure RequestSteamShutdown;
var
  ResultCode: Integer;
  ScriptPath: string;
  Script: string;
begin
  ScriptPath := ExpandConstant('{tmp}\tools-for-steam-shutdown-steam.ps1');
  Script :=
    '$ErrorActionPreference = "SilentlyContinue"' + #13#10 +
    '$steamExe = $null' + #13#10 +
    '$keys = @("HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam")' + #13#10 +
    'foreach ($key in $keys) {' + #13#10 +
    '  $props = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue' + #13#10 +
    '  if (-not $steamExe -and $props.SteamExe) { $steamExe = $props.SteamExe }' + #13#10 +
    '  if (-not $steamExe -and $props.InstallPath) {' + #13#10 +
    '    $candidate = Join-Path $props.InstallPath "steam.exe"' + #13#10 +
    '    if (Test-Path $candidate) { $steamExe = $candidate }' + #13#10 +
    '  }' + #13#10 +
    '}' + #13#10 +
    'if (-not $steamExe) {' + #13#10 +
    '  foreach ($root in @([Environment]::GetFolderPath("ProgramFilesX86"), [Environment]::GetFolderPath("ProgramFiles"))) {' + #13#10 +
    '    if (-not $root) { continue }' + #13#10 +
    '    $candidate = Join-Path $root "Steam\steam.exe"' + #13#10 +
    '    if (Test-Path $candidate) { $steamExe = $candidate; break }' + #13#10 +
    '  }' + #13#10 +
    '}' + #13#10 +
    'if ($steamExe -and (Test-Path $steamExe)) {' + #13#10 +
    '  Start-Process -FilePath $steamExe -ArgumentList "-shutdown" -WindowStyle Hidden' + #13#10 +
    '} else {' + #13#10 +
    '  Start-Process "steam://shutdown"' + #13#10 +
    '}' + #13#10 +
    '$deadline = (Get-Date).AddSeconds(45)' + #13#10 +
    'do {' + #13#10 +
    '  Start-Sleep -Milliseconds 500' + #13#10 +
    '  $processes = Get-Process steam,steamwebhelper -ErrorAction SilentlyContinue' + #13#10 +
    '} while ($processes -and (Get-Date) -lt $deadline)' + #13#10;

  SaveStringToFile(ScriptPath, Script, False);
  Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + ScriptPath + '"',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

procedure ShowFpsHelperInstallWarning(Message: string);
var
  LogPath: string;
begin
  if not WizardSilent then
  begin
    LogPath := ExpandConstant('{app}\data\fps-helper-task.log');
    MsgBox(
      Message + #13#10#13#10 +
      'You can still prepare it later in Performance > TFS FPS Overlay.' + #13#10#13#10 +
      'Details were written to:' + #13#10 + LogPath,
      mbInformation,
      MB_OK);
  end;
end;

function IsFpsHelperTaskInstalled: Boolean;
var
  InstallRoot: string;
  ExePath: string;
  ResultCode: Integer;
begin
  InstallRoot := GetResolvedInstallRootForChecks;
  if InstallRoot = '' then
  begin
    Result := False;
    Exit;
  end;

  ExePath := AddBackslash(InstallRoot) + '{#MyAppExeName}';
  if not FileExists(ExePath) then
  begin
    Result := False;
    Exit;
  end;

  Result := Exec(
    ExePath,
    '--check-fps-helper-task',
    InstallRoot,
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) and (ResultCode = 0);
end;

procedure RegisterFpsHelperTaskSupport;
var
  ResultCode: Integer;
begin
  if not FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
  begin
    Log('TFS FPS helper task registration skipped because the executable is missing.');
    Exit;
  end;

  ForceDirectories(ExpandConstant('{app}\data'));

  if IsFpsHelperTaskInstalled then
  begin
    Log('TFS FPS helper task is already installed.');
    Exit;
  end;

  UpdateSetupStatus('Installing helper: TFS FPS Overlay elevated helper...');

  if not ShellExec(
    'runas',
    ExpandConstant('{app}\{#MyAppExeName}'),
    '--register-fps-helper-task',
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    Log('TFS FPS helper task registration was skipped or cancelled.');
    ShowFpsHelperInstallWarning('The TFS FPS Overlay helper was not prepared during installation because the Windows admin prompt was cancelled or blocked.');
    Exit;
  end;

  if not IsFpsHelperTaskInstalled then
  begin
    Log('TFS FPS helper task registration did not complete successfully.');
    ShowFpsHelperInstallWarning('The TFS FPS Overlay helper could not be prepared during installation.');
  end;
end;

procedure RestoreWindowsShellChrome;
var
  ResultCode: Integer;
begin
  Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command "$code = ''using System; using System.Runtime.InteropServices; public static class W { [DllImport(\"\"user32.dll\"\", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n); [DllImport(\"\"user32.dll\"\", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string n); [DllImport(\"\"user32.dll\"\")] public static extern bool ShowWindow(IntPtr h, int c); }''; try { Add-Type $code -ErrorAction SilentlyContinue } catch {}; $show = 5; $handles = New-Object System.Collections.Generic.List[IntPtr]; $h = [W]::FindWindow(''Shell_TrayWnd'', $null); if ($h -ne [IntPtr]::Zero) { $handles.Add($h) }; $h = [IntPtr]::Zero; while (($h = [W]::FindWindowEx([IntPtr]::Zero, $h, ''Shell_SecondaryTrayWnd'', $null)) -ne [IntPtr]::Zero) { $handles.Add($h) }; foreach ($parent in @([W]::FindWindow(''Progman'', $null))) { if ($parent -ne [IntPtr]::Zero) { $view = [W]::FindWindowEx($parent, [IntPtr]::Zero, ''SHELLDLL_DefView'', $null); if ($view -ne [IntPtr]::Zero) { $handles.Add($view); $list = [W]::FindWindowEx($view, [IntPtr]::Zero, ''SysListView32'', ''FolderView''); if ($list -ne [IntPtr]::Zero) { $handles.Add($list) } } } }; $worker = [IntPtr]::Zero; while (($worker = [W]::FindWindowEx([IntPtr]::Zero, $worker, ''WorkerW'', $null)) -ne [IntPtr]::Zero) { $view = [W]::FindWindowEx($worker, [IntPtr]::Zero, ''SHELLDLL_DefView'', $null); if ($view -ne [IntPtr]::Zero) { $handles.Add($view); $list = [W]::FindWindowEx($view, [IntPtr]::Zero, ''SysListView32'', ''FolderView''); if ($list -ne [IntPtr]::Zero) { $handles.Add($list) } } }; foreach ($handle in $handles) { [W]::ShowWindow($handle, $show) | Out-Null }"',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

function ContainsText(Value: string; Needle: string): Boolean;
begin
  Result := Pos(LowerCase(Needle), LowerCase(Value)) > 0;
end;

function ReadStartupModeFromSettingsPath(SettingsPath: string): string;
var
  SettingsJson: AnsiString;
  SettingsJsonLower: string;
begin
  Result := '';
  if LoadStringFromFile(SettingsPath, SettingsJson) then
  begin
    SettingsJsonLower := LowerCase(string(SettingsJson));
    if Pos('"startupmode"', SettingsJsonLower) > 0 then
    begin
      if Pos('"tray"', SettingsJsonLower) > 0 then
      begin
        Result := 'tray';
      end
      else if Pos('"shell"', SettingsJsonLower) > 0 then
      begin
        Result := 'shell';
      end
      else
      begin
        Result := 'shell';
      end;
    end;
  end;
end;

function ReadStartupModeFromSettings: string;
var
  InstallLocation: string;
begin
  Result := '';
  InstallLocation := GetExistingInstallLocation;
  if InstallLocation <> '' then
  begin
    Result := ReadStartupModeFromSettingsPath(AddBackslash(InstallLocation) + 'data\tfs.json');
    if Result <> '' then
    begin
      Exit;
    end;
  end;

  Result := ReadStartupModeFromSettingsPath(ExpandConstant('{localappdata}\Programs\ToolsForSteam\data\tfs.json'));
end;

function ExistingStartupMode: string;
var
  ShellValue: string;
  RunValue: string;
begin
  Result := '';

  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows NT\CurrentVersion\Winlogon', 'Shell', ShellValue) then
  begin
    if (ContainsText(ShellValue, 'ToolsForSteam.exe') or ContainsText(ShellValue, 'SteamLoader.exe')) and ContainsText(ShellValue, '--shell-bootstrap') then
    begin
      Result := 'shell';
      Exit;
    end;
  end;

  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'TFS', RunValue) then
  begin
    if ContainsText(RunValue, 'ToolsForSteam.exe') or ContainsText(RunValue, 'SteamLoader.exe') then
    begin
      Result := 'tray';
      Exit;
    end;
  end;

  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SteamTools', RunValue) then
  begin
    if ContainsText(RunValue, 'ToolsForSteam.exe') or ContainsText(RunValue, 'SteamLoader.exe') then
    begin
      Result := 'tray';
      Exit;
    end;
  end;

  Result := ReadStartupModeFromSettings;
end;

function SelectedStartupMode: string;
forward;

procedure UpdateSystemCheckPage;
var
  CurrentMode: string;
begin
  CurrentMode := ExistingStartupMode;
  if CurrentMode = '' then
  begin
    CurrentMode := 'clean install';
  end;

  SystemCheckSummaryLabel.Caption :=
    'The installer keeps Shell Mode in front, offers Tray Mode as the lightweight alternative, ' +
    'and creates a rollback snapshot before it changes Windows startup hooks.';

  SystemCheckWindowsLabel.Caption :=
    'Windows build: ' + IntToStr(GetWindowsBuildNumber()) + '.' + IntToStr(GetWindowsBuildRevision()) +
    ' - ready for TFS.';

  if SteamInstallPath <> '' then
  begin
    SystemCheckSteamLabel.Caption := 'Steam: detected automatically at ' + SteamInstallPath + '.';
  end
  else
  begin
    SystemCheckSteamLabel.Caption := 'Steam: not detected from Windows yet. TFS can still be installed and pointed at Steam later.';
  end;

  if ExistingInstallPath <> '' then
  begin
    SystemCheckInstallLabel.Caption := 'Existing TFS install: found at ' + ExistingInstallPath + ' (' + CurrentMode + ' mode).';
  end
  else
  begin
    SystemCheckInstallLabel.Caption := 'Existing TFS install: not found. This will be a fresh install.';
  end;

  SystemCheckHelperLabel.Caption :=
    'Helper: the installer can prepare the elevated TFS FPS helper after files are copied. ' +
    'This check is deferred so the setup window always opens cleanly on fresh Windows installs.';

  SystemCheckRollbackLabel.Caption := 'Rollback: a snapshot and rollback script will be written before startup settings are changed.';
end;

procedure UpdateGuidePage;
begin
  GuideSummaryLabel.Caption :=
    'Controller shortcuts stay simple now. Keep this page in mind so the first launch feels right right away.';
  GuideShellLabel.Caption := 'Shell and Tray Mode: press Guide Button + A to open the TFS side panel.';
  GuideSelectedModeLabel.Caption :=
    'You selected ' + SelectedStartupMode + ' mode. Guide Button + A is the main open-panel shortcut after install.';
end;

procedure UpdateStartupModePageState;
begin
  TrayModeRadio.Visible := True;
  TrayModeDescriptionLabel.Visible := True;
end;

procedure StartupModeSelectionChanged(Sender: TObject);
begin
  UpdateStartupModePageState;
  UpdateGuidePage;
end;

function SelectedStartupMode: string;
begin
#if VariantCustomPages == "0"
  Result := 'shell';
#else
  if WizardSilent then
  begin
    Result := ExistingStartupMode;
    if Result = '' then
    begin
      Result := 'shell';
    end;
  end
  else if TrayModeRadio.Checked then
  begin
    Result := 'tray';
  end
  else
  begin
    Result := 'shell';
  end;
#endif
end;

procedure InitializeWizard;
var
  CurrentMode: string;
  ControlTop: Integer;
begin
#if VariantCustomPages == "0"
  ExistingInstallPath := GetExistingInstallLocation;
  SteamInstallPath := GetSteamInstallLocation;
#else
  CurrentMode := ExistingStartupMode;
  ExistingInstallPath := GetExistingInstallLocation;
  SteamInstallPath := GetSteamInstallLocation;

  SystemCheckPage := CreateCustomPage(
    wpWelcome,
    'System check',
    'Make sure this install lands on the right footing.');

  SystemCheckSummaryLabel := TNewStaticText.Create(WizardForm);
  SystemCheckSummaryLabel.Parent := SystemCheckPage.Surface;
  SystemCheckSummaryLabel.Left := 0;
  SystemCheckSummaryLabel.Top := ScaleY(4);
  SystemCheckSummaryLabel.Width := SystemCheckPage.SurfaceWidth;
  SystemCheckSummaryLabel.Height := ScaleY(56);
  SystemCheckSummaryLabel.AutoSize := False;
  SystemCheckSummaryLabel.WordWrap := True;

  ControlTop := ScaleY(74);

  SystemCheckWindowsLabel := TNewStaticText.Create(WizardForm);
  SystemCheckWindowsLabel.Parent := SystemCheckPage.Surface;
  SystemCheckWindowsLabel.Left := 0;
  SystemCheckWindowsLabel.Top := ControlTop;
  SystemCheckWindowsLabel.Width := SystemCheckPage.SurfaceWidth;
  SystemCheckWindowsLabel.Height := ScaleY(26);
  SystemCheckWindowsLabel.AutoSize := False;
  SystemCheckWindowsLabel.WordWrap := True;

  ControlTop := ControlTop + ScaleY(34);

  SystemCheckSteamLabel := TNewStaticText.Create(WizardForm);
  SystemCheckSteamLabel.Parent := SystemCheckPage.Surface;
  SystemCheckSteamLabel.Left := 0;
  SystemCheckSteamLabel.Top := ControlTop;
  SystemCheckSteamLabel.Width := SystemCheckPage.SurfaceWidth;
  SystemCheckSteamLabel.Height := ScaleY(34);
  SystemCheckSteamLabel.AutoSize := False;
  SystemCheckSteamLabel.WordWrap := True;

  ControlTop := ControlTop + ScaleY(42);

  SystemCheckInstallLabel := TNewStaticText.Create(WizardForm);
  SystemCheckInstallLabel.Parent := SystemCheckPage.Surface;
  SystemCheckInstallLabel.Left := 0;
  SystemCheckInstallLabel.Top := ControlTop;
  SystemCheckInstallLabel.Width := SystemCheckPage.SurfaceWidth;
  SystemCheckInstallLabel.Height := ScaleY(34);
  SystemCheckInstallLabel.AutoSize := False;
  SystemCheckInstallLabel.WordWrap := True;

  ControlTop := ControlTop + ScaleY(42);

  SystemCheckHelperLabel := TNewStaticText.Create(WizardForm);
  SystemCheckHelperLabel.Parent := SystemCheckPage.Surface;
  SystemCheckHelperLabel.Left := 0;
  SystemCheckHelperLabel.Top := ControlTop;
  SystemCheckHelperLabel.Width := SystemCheckPage.SurfaceWidth;
  SystemCheckHelperLabel.Height := ScaleY(34);
  SystemCheckHelperLabel.AutoSize := False;
  SystemCheckHelperLabel.WordWrap := True;

  ControlTop := ControlTop + ScaleY(42);

  SystemCheckRollbackLabel := TNewStaticText.Create(WizardForm);
  SystemCheckRollbackLabel.Parent := SystemCheckPage.Surface;
  SystemCheckRollbackLabel.Left := 0;
  SystemCheckRollbackLabel.Top := ControlTop;
  SystemCheckRollbackLabel.Width := SystemCheckPage.SurfaceWidth;
  SystemCheckRollbackLabel.Height := ScaleY(34);
  SystemCheckRollbackLabel.AutoSize := False;
  SystemCheckRollbackLabel.WordWrap := True;

  StartupModePage := CreateCustomPage(
    SystemCheckPage.ID,
    'Choose startup mode',
    'Shell Mode stays front-and-center. Open the other paths only if you really want them.');

  StartupModeSummaryLabel := TNewStaticText.Create(WizardForm);
  StartupModeSummaryLabel.Parent := StartupModePage.Surface;
  StartupModeSummaryLabel.Left := 0;
  StartupModeSummaryLabel.Top := ScaleY(4);
  StartupModeSummaryLabel.Width := StartupModePage.SurfaceWidth;
  StartupModeSummaryLabel.Height := ScaleY(54);
  StartupModeSummaryLabel.AutoSize := False;
  StartupModeSummaryLabel.WordWrap := True;
  StartupModeSummaryLabel.Caption :=
    'Shell Mode (Recommended) gives the cleanest couch setup, so it is preselected. ' +
    'Tray Mode stays available right below it if you want the lighter Windows-first path instead.';

  ShellModeRadio := TNewRadioButton.Create(WizardForm);
  ShellModeRadio.Parent := StartupModePage.Surface;
  ShellModeRadio.Left := 0;
  ShellModeRadio.Top := ScaleY(78);
  ShellModeRadio.Width := StartupModePage.SurfaceWidth;
  ShellModeRadio.Caption := 'Shell Mode (Recommended)';
  ShellModeRadio.Checked := (CurrentMode = '') or (CurrentMode = 'shell');
  ShellModeRadio.OnClick := @StartupModeSelectionChanged;

  ShellModeDescriptionLabel := TNewStaticText.Create(WizardForm);
  ShellModeDescriptionLabel.Parent := StartupModePage.Surface;
  ShellModeDescriptionLabel.Left := ScaleX(20);
  ShellModeDescriptionLabel.Top := ScaleY(104);
  ShellModeDescriptionLabel.Width := StartupModePage.SurfaceWidth - ScaleX(20);
  ShellModeDescriptionLabel.Height := ScaleY(44);
  ShellModeDescriptionLabel.AutoSize := False;
  ShellModeDescriptionLabel.WordWrap := True;
  ShellModeDescriptionLabel.Caption :=
    'Boot TFS before Explorer, sync launchers, open Steam Big Picture, then put the normal Windows shell back behind Steam.';

  TrayModeRadio := TNewRadioButton.Create(WizardForm);
  TrayModeRadio.Parent := StartupModePage.Surface;
  TrayModeRadio.Left := 0;
  TrayModeRadio.Top := ScaleY(172);
  TrayModeRadio.Width := StartupModePage.SurfaceWidth;
  TrayModeRadio.Caption := 'Tray app mode';
  TrayModeRadio.Checked := CurrentMode = 'tray';
  TrayModeRadio.OnClick := @StartupModeSelectionChanged;

  TrayModeDescriptionLabel := TNewStaticText.Create(WizardForm);
  TrayModeDescriptionLabel.Parent := StartupModePage.Surface;
  TrayModeDescriptionLabel.Left := ScaleX(20);
  TrayModeDescriptionLabel.Top := ScaleY(198);
  TrayModeDescriptionLabel.Width := StartupModePage.SurfaceWidth - ScaleX(20);
  TrayModeDescriptionLabel.Height := ScaleY(36);
  TrayModeDescriptionLabel.AutoSize := False;
  TrayModeDescriptionLabel.WordWrap := True;
  TrayModeDescriptionLabel.Caption :=
    'Keep the normal Windows shell and start TFS quietly with Windows as a tray-style launcher service.';

  GuidePage := CreateCustomPage(
    StartupModePage.ID,
    'Controller guide',
    'One quick page so the first launch already feels natural.');

  GuideSummaryLabel := TNewStaticText.Create(WizardForm);
  GuideSummaryLabel.Parent := GuidePage.Surface;
  GuideSummaryLabel.Left := 0;
  GuideSummaryLabel.Top := ScaleY(4);
  GuideSummaryLabel.Width := GuidePage.SurfaceWidth;
  GuideSummaryLabel.Height := ScaleY(56);
  GuideSummaryLabel.AutoSize := False;
  GuideSummaryLabel.WordWrap := True;

  GuideShellLabel := TNewStaticText.Create(WizardForm);
  GuideShellLabel.Parent := GuidePage.Surface;
  GuideShellLabel.Left := 0;
  GuideShellLabel.Top := ScaleY(82);
  GuideShellLabel.Width := GuidePage.SurfaceWidth;
  GuideShellLabel.Height := ScaleY(40);
  GuideShellLabel.AutoSize := False;
  GuideShellLabel.WordWrap := True;

  GuideSelectedModeLabel := TNewStaticText.Create(WizardForm);
  GuideSelectedModeLabel.Parent := GuidePage.Surface;
  GuideSelectedModeLabel.Left := 0;
  GuideSelectedModeLabel.Top := ScaleY(150);
  GuideSelectedModeLabel.Width := GuidePage.SurfaceWidth;
  GuideSelectedModeLabel.Height := ScaleY(44);
  GuideSelectedModeLabel.AutoSize := False;
  GuideSelectedModeLabel.WordWrap := True;

  if not (ShellModeRadio.Checked or TrayModeRadio.Checked) then
  begin
    ShellModeRadio.Checked := True;
  end;

  UpdateSystemCheckPage;
  UpdateStartupModePageState;
  UpdateGuidePage;
#endif
end;

function IsShellMode: Boolean;
begin
  Result := SelectedStartupMode = 'shell';
end;

function IsTrayMode: Boolean;
begin
  Result := SelectedStartupMode = 'tray';
end;

function BuildPostInstallSettingsArguments: string;
begin
  Result := '--manager';
end;

procedure CreateInstallerBackupSnapshot;
var
  BackupRoot: string;
  SnapshotText: string;
  RollbackScript: string;
  SettingsPath: string;
  SettingsBackupPath: string;
  SettingsJson: AnsiString;
  ExistingShell: string;
  ExistingRunTfs: string;
  ExistingRunSteamTools: string;
begin
  CreatedBackupPath := '';
  ExistingInstallPath := GetExistingInstallLocation;
  if ExistingInstallPath = '' then
  begin
    Log('No previous TFS install was found. Rollback snapshot skipped.');
    Exit;
  end;

  UpdateSetupStatus('Creating rollback snapshot for the existing Tools for Steam setup...');

  BackupRoot := AddBackslash(ExistingInstallPath) + 'data\installer-backups\' + GetDateTimeString('yyyymmdd-hhnnss', '-', ':');
  ForceDirectories(BackupRoot);
  CreatedBackupPath := BackupRoot;

  ExistingShell := GetRegistryStringValue(HKCU, 'Software\Microsoft\Windows NT\CurrentVersion\Winlogon', 'Shell');
  ExistingRunTfs := GetRegistryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'TFS');
  ExistingRunSteamTools := GetRegistryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SteamTools');

  SnapshotText := '';
  AppendLine(SnapshotText, 'existing-install=' + ExistingInstallPath);
  AppendLine(SnapshotText, 'existing-startup-mode=' + ExistingStartupMode);
  AppendLine(SnapshotText, 'steam-path=' + SteamInstallPath);
  AppendLine(SnapshotText, 'shell-value=' + ExistingShell);
  AppendLine(SnapshotText, 'run-tfs=' + ExistingRunTfs);
  AppendLine(SnapshotText, 'run-steamtools=' + ExistingRunSteamTools);
  SaveStringToFile(AddBackslash(BackupRoot) + 'snapshot.txt', SnapshotText, False);

  SettingsPath := AddBackslash(ExistingInstallPath) + 'data\tfs.json';
  SettingsBackupPath := AddBackslash(BackupRoot) + 'tfs.json.backup';
  if LoadStringFromFile(SettingsPath, SettingsJson) then
  begin
    SaveStringToFile(SettingsBackupPath, string(SettingsJson), False);
  end;

  RollbackScript := '';
  AppendLine(RollbackScript, '$ErrorActionPreference = ''Stop''');
  AppendLine(RollbackScript, '$installRoot = ''' + EscapePowerShellSingleQuoted(ExistingInstallPath) + '''');
  AppendLine(RollbackScript, '$settingsBackup = Join-Path $PSScriptRoot ''tfs.json.backup''');
  AppendLine(RollbackScript, '$settingsTarget = Join-Path $installRoot ''data\tfs.json''');
  AppendLine(RollbackScript, 'New-Item -ItemType Directory -Force -Path (Split-Path $settingsTarget) | Out-Null');
  AppendLine(RollbackScript, 'if (Test-Path $settingsBackup) { Copy-Item -Path $settingsBackup -Destination $settingsTarget -Force }');
  AppendLine(RollbackScript, '$winlogonPath = ''HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon''');
  if ExistingShell <> '' then
  begin
    AppendLine(RollbackScript, 'Set-ItemProperty -Path $winlogonPath -Name Shell -Value ''' + EscapePowerShellSingleQuoted(ExistingShell) + '''');
  end
  else
  begin
    AppendLine(RollbackScript, 'Remove-ItemProperty -Path $winlogonPath -Name Shell -ErrorAction SilentlyContinue');
  end;

  AppendLine(RollbackScript, '$runPath = ''HKCU:\Software\Microsoft\Windows\CurrentVersion\Run''');
  if ExistingRunTfs <> '' then
  begin
    AppendLine(RollbackScript, 'Set-ItemProperty -Path $runPath -Name TFS -Value ''' + EscapePowerShellSingleQuoted(ExistingRunTfs) + '''');
  end
  else
  begin
    AppendLine(RollbackScript, 'Remove-ItemProperty -Path $runPath -Name TFS -ErrorAction SilentlyContinue');
  end;

  if ExistingRunSteamTools <> '' then
  begin
    AppendLine(RollbackScript, 'Set-ItemProperty -Path $runPath -Name SteamTools -Value ''' + EscapePowerShellSingleQuoted(ExistingRunSteamTools) + '''');
  end
  else
  begin
    AppendLine(RollbackScript, 'Remove-ItemProperty -Path $runPath -Name SteamTools -ErrorAction SilentlyContinue');
  end;

  AppendLine(RollbackScript, 'Write-Host ''Tools for Steam startup settings were restored from this snapshot.''');
  SaveStringToFile(AddBackslash(BackupRoot) + 'rollback-toolsforsteam.ps1', RollbackScript, False);
  SaveStringToFile(
    AddBackslash(BackupRoot) + 'README.txt',
    'Run rollback-toolsforsteam.ps1 if you want to restore the previous startup hooks and settings snapshot.' + #13#10,
    False);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
#if VariantCustomPages == "1"
  if CurPageID = SystemCheckPage.ID then
  begin
    UpdateSystemCheckPage;
  end;

  if CurPageID = StartupModePage.ID then
  begin
    UpdateStartupModePageState;
  end;

  if CurPageID = GuidePage.ID then
  begin
    UpdateGuidePage;
  end;
#endif

  if (CurPageID = wpFinished) and (not WizardSilent) then
  begin
    LaunchSettingsAfterSetup := True;
    PostInstallSettingsArguments := BuildPostInstallSettingsArguments;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
#if VariantFpsHelperPrep == "1"
    RegisterFpsHelperTaskSupport;
#endif
  end;
end;

procedure DeinitializeSetup;
var
  ResultCode: Integer;
begin
  if not LaunchSettingsAfterSetup then
  begin
    Exit;
  end;

  if not FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
  begin
    Exit;
  end;

  Exec(
    ExpandConstant('{app}\{#MyAppExeName}'),
    PostInstallSettingsArguments,
    ExpandConstant('{app}'),
    SW_SHOWNORMAL,
    ewNoWait,
    ResultCode);
end;

function NeedRestart: Boolean;
begin
  Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CreateInstallerBackupSnapshot;
  UpdateSetupStatus('Closing running Tools for Steam and Steam sessions before install...');
  RequestToolsForSteamShutdown;
  Sleep(2000);
  RestoreWindowsShellChrome;
  CloseProcess('ToolsForSteam.exe');
  CloseProcess('SteamLoader.exe');
  RequestSteamShutdown;
  RestoreWindowsShellChrome;
  Result := '';
end;
