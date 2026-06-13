#define MyAppName "Tools for Steam"
#define MyAppShortName "TFS"
#define MyAppVersion "0.3.1"
#define MyAppPublisher "GCM - Gaming Console Mode"
#define MyAppExeName "ToolsForSteam.exe"
#define MyAppId "{{9A9F0B7E-4C79-4C7D-8E4B-0E0D766E0B72}"
#define PayloadDir "..\dist\portable"

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
OutputBaseFilename=ToolsForSteamSetup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0
SetupMutex=ToolsForSteamSetup
CloseApplications=force
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
Filename: "{app}\{#MyAppExeName}"; Parameters: "--tray --startup-sync --shell-bootstrap"; Description: "Start Tools for Steam"; Flags: nowait runasoriginaluser postinstall skipifsilent skipifdoesntexist; Check: IsShellMode
Filename: "{app}\{#MyAppExeName}"; Parameters: "--tray --startup-sync"; Description: "Start Tools for Steam"; Flags: nowait runasoriginaluser postinstall skipifsilent skipifdoesntexist; Check: IsTrayMode
Filename: "{app}\{#MyAppExeName}"; Parameters: "--tray --startup-sync --shell-bootstrap"; Flags: nowait runasoriginaluser skipifnotsilent skipifdoesntexist; Check: IsShellMode
Filename: "{app}\{#MyAppExeName}"; Parameters: "--tray --startup-sync"; Flags: nowait runasoriginaluser skipifnotsilent skipifdoesntexist; Check: IsTrayMode

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM ToolsForSteam.exe /T /F"; Flags: runhidden waituntilterminated; RunOnceId: "CloseToolsForSteam"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM SteamLoader.exe /T /F"; Flags: runhidden waituntilterminated; RunOnceId: "CloseLegacySteamLoader"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM steam.exe /T /F"; Flags: runhidden waituntilterminated; RunOnceId: "CloseSteam"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM steamwebhelper.exe /T /F"; Flags: runhidden waituntilterminated; RunOnceId: "CloseSteamWebHelper"

[Code]
var
  StartupModePage: TInputOptionWizardPage;

procedure CloseProcess(ImageName: string);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM "' + ImageName + '" /T /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
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
      else if Pos('"manual"', SettingsJsonLower) > 0 then
      begin
        Result := 'manual';
      end;
    end;
  end;
end;

function ReadStartupModeFromSettings: string;
var
  InstallLocation: string;
begin
  Result := '';

  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppId}_is1', 'InstallLocation', InstallLocation) then
  begin
    Result := ReadStartupModeFromSettingsPath(AddBackslash(InstallLocation) + 'data\tfs.json');
    if Result <> '' then
    begin
      Exit;
    end;
  end;

  if RegQueryStringValue(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppId}_is1', 'InstallLocation', InstallLocation) then
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
begin
  if WizardSilent then
  begin
    Result := ExistingStartupMode;
    if Result = '' then
    begin
      Result := 'shell';
    end;
  end
  else if StartupModePage.Values[1] then
  begin
    Result := 'tray';
  end
  else
  begin
    Result := 'shell';
  end;
end;

procedure InitializeWizard;
var
  CurrentMode: string;
begin
  StartupModePage := CreateInputOptionPage(
    wpSelectTasks,
    'Choose startup mode',
    'How should Tools for Steam start with Windows?',
    'Shell takeover gives the closest SteamOS-style boot. Tray app keeps the normal Windows shell and starts Tools for Steam from autorun.',
    True,
    False);

  StartupModePage.Add('Shell takeover mode - start before Explorer, sync launchers, open Steam Big Picture, then restore Windows behind Steam.');
  StartupModePage.Add('Tray app mode - start with Windows as a tray app, sync launchers, and open Steam without replacing the Windows shell.');
  CurrentMode := ExistingStartupMode;
  StartupModePage.Values[0] := CurrentMode <> 'tray';
  StartupModePage.Values[1] := CurrentMode = 'tray';
end;

function IsShellMode: Boolean;
begin
  Result := SelectedStartupMode = 'shell';
end;

function IsTrayMode: Boolean;
begin
  Result := SelectedStartupMode = 'tray';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseProcess('ToolsForSteam.exe');
  CloseProcess('SteamLoader.exe');
  CloseProcess('steam.exe');
  CloseProcess('steamwebhelper.exe');
  Result := '';
end;
