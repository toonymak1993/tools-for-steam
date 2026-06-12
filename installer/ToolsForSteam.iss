#define MyAppName "Tools for Steam"
#define MyAppShortName "TFS"
#define MyAppVersion "0.3.0"
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
Filename: "{app}\{#MyAppExeName}"; Parameters: "--tray --startup-sync --shell-bootstrap"; Description: "Start Tools for Steam"; Flags: nowait runasoriginaluser postinstall skipifsilent skipifdoesntexist
Filename: "{app}\{#MyAppExeName}"; Parameters: "--tray --startup-sync --shell-bootstrap"; Flags: nowait runasoriginaluser skipifnotsilent skipifdoesntexist

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM ToolsForSteam.exe /T /F"; Flags: runhidden waituntilterminated; RunOnceId: "CloseToolsForSteam"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM SteamLoader.exe /T /F"; Flags: runhidden waituntilterminated; RunOnceId: "CloseLegacySteamLoader"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM steam.exe /T /F"; Flags: runhidden waituntilterminated; RunOnceId: "CloseSteam"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM steamwebhelper.exe /T /F"; Flags: runhidden waituntilterminated; RunOnceId: "CloseSteamWebHelper"

[Code]
procedure CloseProcess(ImageName: string);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM "' + ImageName + '" /T /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseProcess('ToolsForSteam.exe');
  CloseProcess('SteamLoader.exe');
  CloseProcess('steam.exe');
  CloseProcess('steamwebhelper.exe');
  Result := '';
end;
