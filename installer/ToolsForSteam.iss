#define MyAppName "Tools for Steam"
#define MyAppShortName "TFS"
#define MyAppVersion "0.2.0"
#define MyAppBinaryVersion "0.2.0.0"
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
#ifndef VariantFreshInstallPreview
  #define VariantFreshInstallPreview "0"
#endif
#ifndef VariantUiPreview
  #define VariantUiPreview "0"
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
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Installer
VersionInfoOriginalFileName=ToolsForSteamSetup.exe
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppBinaryVersion}
VersionInfoProductTextVersion={#MyAppVersion}
VersionInfoTextVersion={#MyAppVersion}
VersionInfoVersion={#MyAppBinaryVersion}
DefaultDirName={localappdata}\Programs\ToolsForSteam
DefaultGroupName={#MyAppName}
DisableWelcomePage=no
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE.txt
OutputDir=..\dist\installer
OutputBaseFilename={#VariantOutputBaseFilename}
Compression=lzma2/max
SolidCompression=yes
WizardStyle={#VariantWizardStyle} dynamic polar includetitlebar hidebevels
WizardSizePercent=110
SetupIconFile=Assets\AppIcon.ico
WizardImageFile=Assets\WizardHero.png
WizardImageFileDynamicDark=Assets\WizardHero.png
WizardImageBackColor=#10161F
WizardImageBackColorDynamicDark=#10161F
WizardSmallImageFile=Assets\WizardSmallLogo.png
WizardSmallImageFileDynamicDark=Assets\WizardSmallLogo.png
WizardSmallImageBackColor=#10161F
WizardSmallImageBackColorDynamicDark=#10161F
#if VariantUiPreview == "1"
PrivilegesRequired=lowest
#else
PrivilegesRequired=admin
#endif
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SetupMutex=ToolsForSteamSetup
#ifdef TfsSign
; publish-installer.ps1 defines the "tfssign" tool (Certum SimplySign) with /S.
SignTool=tfssign
SignedUninstaller=yes
#endif
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
english.TfsSystemCheckCaption=System ready
german.TfsSystemCheckCaption=System bereit
english.TfsSystemCheckDescription=Everything important at a glance. Technical details stay available when you need them.
german.TfsSystemCheckDescription=Alles Wichtige auf einen Blick. Technische Details bleiben bei Bedarf verfügbar.
english.TfsSystemCheckSummary=Setup checks Windows, Steam, and an existing Tools for Steam installation before making any changes.
german.TfsSystemCheckSummary=Setup prüft Windows, Steam und eine vorhandene Tools-for-Steam-Installation, bevor Änderungen vorgenommen werden.
english.TfsStatusReady=READY
german.TfsStatusReady=BEREIT
english.TfsStatusNotice=NOTICE
german.TfsStatusNotice=HINWEIS
english.TfsWindowsReady=Windows %1.%2 supports Tools for Steam.
german.TfsWindowsReady=Windows %1.%2 unterstützt Tools for Steam.
english.TfsSteamFound=Steam was detected automatically at %1.
german.TfsSteamFound=Steam wurde automatisch unter %1 gefunden.
english.TfsSteamMissing=Steam was not detected yet. You can select it later inside Tools for Steam.
german.TfsSteamMissing=Steam wurde noch nicht erkannt. Der Pfad kann später in Tools for Steam ausgewählt werden.
english.TfsInstallFound=Existing installation found at %1. Current startup mode: %2.
german.TfsInstallFound=Vorhandene Installation unter %1 gefunden. Aktueller Startmodus: %2.
english.TfsInstallFresh=No existing installation was found. This will be a clean installation.
german.TfsInstallFresh=Keine vorhandene Installation gefunden. Dies wird eine Neuinstallation.
english.TfsShowDetails=Show technical details
german.TfsShowDetails=Technische Details anzeigen
english.TfsHideDetails=Hide technical details
german.TfsHideDetails=Technische Details ausblenden
english.TfsPerformanceDetails=Performance support reuses existing RTSS files and settings. Setup downloads RTSS 7.3.7 only when it is missing and configures per-user profile access.
german.TfsPerformanceDetails=Die Performance-Unterstützung verwendet vorhandene RTSS-Dateien und Einstellungen. Setup lädt RTSS 7.3.7 nur herunter, wenn es fehlt, und richtet den Profilzugriff pro Benutzer ein.
english.TfsRollbackDetails=A rollback snapshot and recovery script are created before any Windows startup settings are changed.
german.TfsRollbackDetails=Bevor Windows-Starteinstellungen geändert werden, werden ein Wiederherstellungspunkt und ein Rollback-Skript erstellt.
english.TfsModeCaption=Choose your experience
german.TfsModeCaption=Nutzungserlebnis auswählen
english.TfsModeDescription=Choose how Tools for Steam should start. You can change this later in the app.
german.TfsModeDescription=Lege fest, wie Tools for Steam starten soll. Dies kann später in der App geändert werden.
english.TfsModeSummary=Console Mode is recommended for a console-like couch setup and keeps Windows untouched. eTray keeps the normal Windows desktop.
german.TfsModeSummary=Der Console Mode wird für ein konsolenähnliches Wohnzimmer-Erlebnis empfohlen und lässt Windows unverändert. eTray behält den normalen Windows-Desktop bei.
english.TfsConsoleTitle=Console Mode  ·  RECOMMENDED
german.TfsConsoleTitle=Console Mode  ·  EMPFOHLEN
english.TfsConsoleDescription=A sign-in task starts TFS and Steam Big Picture right after you sign in and covers the desktop until Steam is ready. Explorer stays the Windows shell.
german.TfsConsoleDescription=Eine Anmeldeaufgabe startet TFS und Steam Big Picture direkt nach der Windows-Anmeldung und verdeckt den Desktop, bis Steam bereit ist. Der Explorer bleibt die Windows-Oberfläche.
english.TfsHandheldClaw=%1 detected. Setup installs TDP control (PawnIO), the controller drivers (USBIP, HidHide) and the elevated hardware helper.
german.TfsHandheldClaw=%1 erkannt. Setup installiert die TDP-Steuerung (PawnIO), die Controller-Treiber (USBIP, HidHide) und den Hardware-Helfer mit Admin-Rechten.
english.TfsHandheldAmd=%1 with %2 detected. Setup installs TDP control (PawnIO) and the elevated hardware helper.
german.TfsHandheldAmd=%1 mit %2 erkannt. Setup installiert die TDP-Steuerung (PawnIO) und den Hardware-Helfer mit Admin-Rechten.
english.TfsHandheldNone=No handheld with TDP control was detected. Setup installs the elevated controller helper and skips handheld components.
german.TfsHandheldNone=Kein Handheld mit TDP-Steuerung erkannt. Setup installiert den Controller-Helfer mit Admin-Rechten und überspringt Handheld-Komponenten.
english.TfsHelperDetails=Setup registers a hidden Task Scheduler entry (\ToolsForSteam\GamepadHelper) that runs the Tools for Steam hardware helper with admin rights for controller input and TDP control, so the app never needs a UAC prompt for them. Console Mode adds a visible sign-in task for the current user only.
german.TfsHelperDetails=Setup registriert eine versteckte Aufgabe (\ToolsForSteam\GamepadHelper), die den Hardware-Helfer von Tools for Steam mit Admin-Rechten für Controller-Eingaben und die TDP-Steuerung ausführt, damit die App dafür nie eine UAC-Abfrage braucht. Der Console Mode ergänzt eine sichtbare Anmeldeaufgabe nur für den aktuellen Benutzer.
english.TfsWebView2Details=Store sign-ins need the Microsoft Edge WebView2 Runtime. Setup installs it from Microsoft only when it is missing.
german.TfsWebView2Details=Store-Anmeldungen brauchen die Microsoft Edge WebView2 Runtime. Setup installiert sie nur dann von Microsoft, wenn sie fehlt.
english.TfsTrayTitle=eTray
german.TfsTrayTitle=eTray
english.TfsTrayDescription=Keeps the normal Windows desktop and starts TFS quietly in the background with Windows.
german.TfsTrayDescription=Behält den normalen Windows-Desktop bei und startet TFS unauffällig mit Windows im Hintergrund.
english.TfsWelcomeTitle=Welcome to Tools for Steam
german.TfsWelcomeTitle=Willkommen bei Tools for Steam
english.TfsWelcomeBody=Turn Windows into a controller-friendly Steam console experience. Setup will check compatibility, protect your current configuration, and guide you through the startup mode.
german.TfsWelcomeBody=Verwandle Windows in ein controllerfreundliches Steam-Konsolenerlebnis. Setup prüft die Kompatibilität, schützt die vorhandene Konfiguration und führt durch die Auswahl des Startmodus.
english.TfsUpdateWelcomeTitle=Update Tools for Steam
german.TfsUpdateWelcomeTitle=Tools for Steam aktualisieren
english.TfsUpdateWelcomeBody=Your current startup mode and settings will be preserved. Setup creates a rollback snapshot before replacing files.
german.TfsUpdateWelcomeBody=Der aktuelle Startmodus und alle Einstellungen bleiben erhalten. Vor dem Ersetzen von Dateien erstellt Setup einen Wiederherstellungspunkt.
english.TfsFinishedTitle=Tools for Steam is ready
german.TfsFinishedTitle=Tools for Steam ist bereit
english.TfsFinishedBody=Installation completed successfully. Your selected startup mode is configured and can be changed later inside Tools for Steam.
german.TfsFinishedBody=Die Installation wurde erfolgreich abgeschlossen. Der gewählte Startmodus ist eingerichtet und kann später in Tools for Steam geändert werden.
english.TfsUpdateFinishedBody=The update completed successfully. Your startup mode and settings were preserved, and a rollback snapshot was created before files were replaced.
german.TfsUpdateFinishedBody=Das Update wurde erfolgreich abgeschlossen. Startmodus und Einstellungen wurden beibehalten; vor dem Ersetzen der Dateien wurde ein Wiederherstellungspunkt erstellt.
english.TfsReadyCaption=Ready to install
german.TfsReadyCaption=Bereit zur Installation
english.TfsReadyDescription=Review your choices below. Setup will not change Windows startup settings until you select Install now.
german.TfsReadyDescription=Prüfe unten die Auswahl. Windows-Starteinstellungen werden erst nach „Jetzt installieren“ geändert.
english.TfsUpdateReadyCaption=Ready to update
german.TfsUpdateReadyCaption=Bereit zum Update
english.TfsUpdateReadyDescription=Your existing installation was detected. Settings and the current startup mode will be preserved.
german.TfsUpdateReadyDescription=Die vorhandene Installation wurde erkannt. Einstellungen und aktueller Startmodus bleiben erhalten.
english.TfsReadyPrompt=Tools for Steam is ready to be installed with the choices shown below.
german.TfsReadyPrompt=Tools for Steam kann jetzt mit der unten angezeigten Auswahl installiert werden.
english.TfsUpdateReadyPrompt=Tools for Steam is ready to update. No existing settings will be reset.
german.TfsUpdateReadyPrompt=Tools for Steam kann jetzt aktualisiert werden. Vorhandene Einstellungen werden nicht zurückgesetzt.
english.TfsInstallNow=Install now
german.TfsInstallNow=Jetzt installieren
english.TfsUpdateNow=Update now
german.TfsUpdateNow=Jetzt aktualisieren
english.TfsContinue=Continue
german.TfsContinue=Weiter
english.TfsInstallingCaption=Setting up Tools for Steam
german.TfsInstallingCaption=Tools for Steam wird eingerichtet
english.TfsUpdatingCaption=Updating Tools for Steam
german.TfsUpdatingCaption=Tools for Steam wird aktualisiert
english.TfsPreviewStop=UI preview complete. This preview cannot install or change anything on your system.
german.TfsPreviewStop=UI-Vorschau abgeschlossen. Diese Vorschau kann nichts installieren oder an deinem System ändern.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion restartreplace
Source: "{#PayloadDir}\{#MyAppExeName}"; DestName: "ToolsForSteamUpdateHelper.exe"; Flags: dontcopy noencryption
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\SteamLoader.App\ThirdParty\PawnIO\COPYING"; DestDir: "{app}\ThirdParty\PawnIO"; DestName: "COPYING-LGPL-2.1.txt"; Flags: ignoreversion
Source: "..\src\SteamLoader.App\ThirdParty\PawnIO\NOTICE.txt"; DestDir: "{app}\ThirdParty\PawnIO"; Flags: ignoreversion
Source: "..\src\SteamLoader.App\ThirdParty\PawnIO\PawnIO_setup.exe"; DestDir: "{app}\ThirdParty\PawnIO"; Flags: ignoreversion
Source: "..\src\SteamLoader.App\ThirdParty\DiscordSocialSdk\License-Notices.txt"; DestDir: "{app}\ThirdParty\DiscordSocialSdk"; Flags: ignoreversion
Source: "..\src\SteamLoader.App\ThirdParty\DiscordSocialSdk\NOTICE.txt"; DestDir: "{app}\ThirdParty\DiscordSocialSdk"; Flags: ignoreversion
Source: "..\src\SteamLoader.App\ThirdParty\Legendary\NOTICE.txt"; DestDir: "{app}\ThirdParty\Legendary"; Flags: ignoreversion
Source: "..\src\SteamLoader.App\ThirdParty\GogDl\NOTICE.txt"; DestDir: "{app}\ThirdParty\GogDl"; Flags: ignoreversion
Source: "..\dist\handheld-runtime\VIIPER\viiper.exe"; DestDir: "{app}\ThirdParty\VIIPER"; Flags: ignoreversion
Source: "..\dist\handheld-runtime\VIIPER\licenses.txt"; DestDir: "{app}\ThirdParty\VIIPER"; Flags: ignoreversion
Source: "..\dist\handheld-runtime\VIIPER\VIIPER-v0.7.0-source.zip"; DestDir: "{app}\ThirdParty\VIIPER"; Flags: ignoreversion
Source: "..\dist\handheld-runtime\Drivers\USBip-0.9.7.8-x64.exe"; DestDir: "{app}\ThirdParty\HandheldDrivers"; Flags: ignoreversion
Source: "..\dist\handheld-runtime\Drivers\HidHide_1.5.230_x64.exe"; DestDir: "{app}\ThirdParty\HandheldDrivers"; Flags: ignoreversion

[InstallDelete]
Type: files; Name: "{app}\SteamLoader.exe"
Type: files; Name: "{app}\SteamLoader.dll"
Type: files; Name: "{app}\SteamLoader.pdb"
Type: files; Name: "{app}\SteamLoader.deps.json"
Type: files; Name: "{app}\SteamLoader.runtimeconfig.json"
Type: files; Name: "{app}\install-toolsforsteam.ps1"
Type: files; Name: "{app}\uninstall-toolsforsteam.ps1"
Type: files; Name: "{app}\data\performance-runtime.json"
Type: files; Name: "{app}\data\viiper-runtime.log"
Type: filesandordirs; Name: "{app}\data\viiper-profile"
Type: filesandordirs; Name: "{app}\data\oem-shortcut-quarantine"
; Xbox Mode (Gaming Home package) was removed; drop its old payload.
Type: filesandordirs; Name: "{app}\XboxMode"
; One Start menu entry starts TFS; older installs also had a duplicate.
Type: files; Name: "{group}\Start Tools for Steam.lnk"

[Icons]
; The exe carries the TFS hex emblem (src\SteamLoader.App\AppIcon.ico).
Name: "{group}\Tools for Steam"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--tray"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Comment: "Start Tools for Steam"
Name: "{group}\Uninstall Tools for Steam"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Tools for Steam"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--tray"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\GCM\SteamTools"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletevalue uninsdeletekeyifempty

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM ToolsForSteam.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "CloseToolsForSteam"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM SteamLoader.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "CloseLegacySteamLoader"
; Give Windows its shell back and drop the Run value and the Console Mode
; sign-in task; otherwise the next sign-in would start a deleted executable.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--remove-startup-integration"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveStartupIntegration"
Filename: "{app}\{#MyAppExeName}"; Parameters: "--restore-handheld-replacement"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RestoreHandheldReplacement"
Filename: "{app}\{#MyAppExeName}"; Parameters: "--remove-owned-handheld-drivers"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveOwnedHandheldDrivers"
; The Xbox Mode integration was removed. These entries still clean up after
; installs that predate the removal (Gaming Home settings, package, certificates).
Filename: "{app}\{#MyAppExeName}"; Parameters: "--restore-xbox-mode"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RestoreXboxModeSettings"
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ""Get-AppxPackage -Name 'GCM.ToolsForSteam.XboxHost' | Remove-AppxPackage"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveXboxModePackage"
Filename: "{sys}\certutil.exe"; Parameters: "-delstore TrustedPeople F12969BBD718F0BDAE024A02C716BEBF39141CA7"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveXboxModeCertificate"
Filename: "{sys}\certutil.exe"; Parameters: "-delstore TrustedPeople E38C2E0BDF195C5F4329102504002F4E0766FF99"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveLegacyXboxModeCertificate"
Filename: "{sys}\certutil.exe"; Parameters: "-delstore TrustedPeople B9FB8BD316D7A0FB2AC6AC10F204D562F9F8E251"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveOlderXboxModeCertificate"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""\ToolsForSteam\GamepadHelper"" /F"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveGamepadHelperTask"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""\ToolsForSteam\FpsHelper"" /F"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveFpsHelperTask"

[Code]
const
  { Retired Xbox Mode (Gaming Home) package. Only used to remove what older
    installs left behind. }
  LegacyXboxHostPackageName = 'GCM.ToolsForSteam.XboxHost';
  LegacyXboxHostCertificateThumbprints = 'F12969BBD718F0BDAE024A02C716BEBF39141CA7,E38C2E0BDF195C5F4329102504002F4E0766FF99,B9FB8BD316D7A0FB2AC6AC10F204D562F9F8E251';
  PawnIOSetupSha256 = '1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032';
  UsbIpSetupSha256 = '44451fe06f4186125c2a5ecd25b099c5560a61a60b1e56f5a0758e77a60afa44';
  HidHideSetupSha256 = 'f4bbbcb82e6258641b887c74bc81c4c5f66e4aa811808dfc304347687b7605f6';
  RtssPackageId = 'Guru3D.RTSS';
  RtssRequiredVersion = '7.3.7';
  WebView2ClientKey = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2BootstrapperUrl = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';

var
  SystemCheckPage: TWizardPage;
  StartupModePage: TWizardPage;
  SystemCheckWindowsPanel: TPanel;
  SystemCheckSteamPanel: TPanel;
  SystemCheckInstallPanel: TPanel;
  SystemCheckSummaryLabel: TNewStaticText;
  SystemCheckWindowsLabel: TNewStaticText;
  SystemCheckSteamLabel: TNewStaticText;
  SystemCheckInstallLabel: TNewStaticText;
  SystemCheckHandheldPanel: TPanel;
  SystemCheckHandheldLabel: TNewStaticText;
  SystemCheckDetailsButton: TNewButton;
  ConsoleModePanel: TPanel;
  ConsoleModeRadio: TNewRadioButton;
  ConsoleModeDescriptionLabel: TNewStaticText;
  TrayModePanel: TPanel;
  StartupModeSummaryLabel: TNewStaticText;
  TrayModeRadio: TNewRadioButton;
  TrayModeDescriptionLabel: TNewStaticText;
  LaunchSettingsAfterSetup: Boolean;
  PostInstallSettingsArguments: string;
  ExistingInstallPath: string;
  SteamInstallPath: string;
  CreatedBackupPath: string;
  InstallFinalizationReady: Boolean;
  UpdateInstallRequested: Boolean;
  InitialInstallWasUpdate: Boolean;
  UpdateStartupModeToRestore: string;
  StartupModeFinalized: Boolean;
  FinalizedStartupMode: string;
  DiagnosticFailureReason: string;
  HandheldDriversInstalledThisRun: Boolean;
  ElevatedHelperTasksSuspended: Boolean;

function IsMsiClawA8: Boolean; forward;
function SelectedStartupMode: string; forward;
function IsUpdateOrUpgradeInstall: Boolean; forward;

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

function DiagnosticBool(Value: Boolean): string;
begin
  if Value then
  begin
    Result := 'true';
  end
  else
  begin
    Result := 'false';
  end;
end;

procedure RecordDiagnosticFailure(Message: string);
begin
  if DiagnosticFailureReason = '' then
  begin
    DiagnosticFailureReason := Message;
  end
  else
  begin
    DiagnosticFailureReason := DiagnosticFailureReason + ' | ' + Message;
  end;
  Log('Diagnostic failure recorded: ' + Message);
end;

function DescribeStartupMode(Mode: string): string;
begin
  if Mode = 'console' then
  begin
    Result := 'Console';
  end
  else if Mode = 'tray' then
  begin
    Result := 'Tray';
  end
  else
  begin
    Result := Mode;
  end;
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

function IsLegacyXboxHostCertificateInstalled(Thumbprint: string): Boolean;
begin
  Result := RegKeyExists(
    HKLM64,
    'SOFTWARE\Microsoft\SystemCertificates\TrustedPeople\Certificates\' + Thumbprint);
end;

procedure RemoveLegacyXboxModeIntegration;
var
  ResultCode: Integer;
  Thumbprints: string;
  Thumbprint: string;
  SeparatorIndex: Integer;
begin
  { Xbox Mode (TFS as Windows Gaming Home app) was removed. Point Windows away
    from the retired package, restoring the values from before TFS took over,
    then remove the package and its signing certificates. Every step is best
    effort: a leftover from the old integration must never block the install. }
  if FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
  begin
    Exec(
      ExpandConstant('{app}\{#MyAppExeName}'),
      '--restore-xbox-mode',
      ExpandConstant('{app}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode);
  end;

  if DirExists(ExpandConstant('{localappdata}\Packages\GCM.ToolsForSteam.XboxHost_kpg9gzy2ksp2j')) then
  begin
    UpdateSetupStatus('Removing the retired Tools for Steam Xbox Mode package...');
    if Exec(
        ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
        '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command "Get-AppxPackage -Name ''' + LegacyXboxHostPackageName + ''' | Remove-AppxPackage -ErrorAction Stop"',
        '',
        SW_HIDE,
        ewWaitUntilTerminated,
        ResultCode) and (ResultCode = 0) then
    begin
      Log('The retired Xbox Mode package was removed.');
    end
    else
    begin
      Log('The retired Xbox Mode package could not be removed (code ' + IntToStr(ResultCode) + '). It no longer starts Tools for Steam.');
    end;
  end;

  Thumbprints := LegacyXboxHostCertificateThumbprints + ',';
  while Thumbprints <> '' do
  begin
    SeparatorIndex := Pos(',', Thumbprints);
    Thumbprint := Copy(Thumbprints, 1, SeparatorIndex - 1);
    Delete(Thumbprints, 1, SeparatorIndex);
    if (Thumbprint <> '') and IsLegacyXboxHostCertificateInstalled(Thumbprint) then
    begin
      Exec(
        ExpandConstant('{sys}\certutil.exe'),
        '-delstore TrustedPeople "' + Thumbprint + '"',
        '',
        SW_HIDE,
        ewWaitUntilTerminated,
        ResultCode);
      Log('Retired Xbox Mode certificate ' + Thumbprint + ' removal returned code ' + IntToStr(ResultCode) + '.');
    end;
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

function StopElevatedHelperTasksForInstall: Boolean;
var
  ScriptPath: string;
  Script: string;
  ResultCode: Integer;
begin
  ScriptPath := ExpandConstant('{tmp}\tools-for-steam-stop-elevated-helpers.ps1');
  Script :=
    '$ErrorActionPreference = "SilentlyContinue"' + #13#10 +
    '$tasks = @("\ToolsForSteam\GamepadHelper", "\ToolsForSteam\FpsHelper")' + #13#10 +
    'foreach ($task in $tasks) {' + #13#10 +
    '  & "$env:WINDIR\System32\schtasks.exe" /Change /TN $task /Disable 2>$null | Out-Null' + #13#10 +
    '  & "$env:WINDIR\System32\schtasks.exe" /End /TN $task 2>$null | Out-Null' + #13#10 +
    '}' + #13#10 +
    'Start-Sleep -Milliseconds 500' + #13#10 +
    '$helpers = Get-CimInstance Win32_Process | Where-Object {' + #13#10 +
    '  $_.Name -eq "ToolsForSteam.exe" -and $_.CommandLine -match "--(gamepad|fps)-helper"' + #13#10 +
    '}' + #13#10 +
    '$helpers | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }' + #13#10 +
    'Start-Sleep -Milliseconds 500' + #13#10 +
    '$remaining = Get-CimInstance Win32_Process | Where-Object {' + #13#10 +
    '  $_.Name -eq "ToolsForSteam.exe" -and $_.CommandLine -match "--(gamepad|fps)-helper"' + #13#10 +
    '}' + #13#10 +
    'if ($remaining) { exit 1 }' + #13#10 +
    'exit 0' + #13#10;

  Result :=
    SaveStringToFile(ScriptPath, Script, False) and
    Exec(
      ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + ScriptPath + '"',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and
    (ResultCode = 0);
  ElevatedHelperTasksSuspended := Result;
end;

procedure ResumeElevatedHelperTasks;
var
  ResultCode: Integer;
begin
  if not ElevatedHelperTasksSuspended then
  begin
    Exit;
  end;

  Exec(
    ExpandConstant('{sys}\schtasks.exe'),
    '/Change /TN "\ToolsForSteam\GamepadHelper" /Enable',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
  ElevatedHelperTasksSuspended := False;
end;

function EnsureToolsForSteamRuntimeStopped: Boolean;
var
  ResultCode: Integer;
  ScriptPath: string;
  Script: string;
begin
  ScriptPath := ExpandConstant('{tmp}\tools-for-steam-stop-runtime.ps1');
  Script :=
    '$ErrorActionPreference = "SilentlyContinue"' + #13#10 +
    '$names = @("ToolsForSteam", "SteamLoader", "ToolsForSteam.XboxHost")' + #13#10 +
    '$deadline = (Get-Date).AddSeconds(12)' + #13#10 +
    'do {' + #13#10 +
    '  $processes = Get-Process -Name $names -ErrorAction SilentlyContinue' + #13#10 +
    '  if (-not $processes) { exit 0 }' + #13#10 +
    '  $processes | Stop-Process -Force -ErrorAction SilentlyContinue' + #13#10 +
    '  Start-Sleep -Milliseconds 350' + #13#10 +
    '} while ((Get-Date) -lt $deadline)' + #13#10 +
    'if (Get-Process -Name $names -ErrorAction SilentlyContinue) { exit 1 }' + #13#10 +
    'exit 0' + #13#10;

  Result :=
    SaveStringToFile(ScriptPath, Script, False) and
    Exec(
      ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + ScriptPath + '"',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and
    (ResultCode = 0);
end;

function WaitForToolsForSteamExecutableRelease: Boolean;
var
  ExePath: string;
  ScriptPath: string;
  Script: string;
  ResultCode: Integer;
begin
  ExePath := AddBackslash(ExpandConstant('{app}')) + '{#MyAppExeName}';
  if not FileExists(ExePath) then
  begin
    Result := True;
    Exit;
  end;

  ScriptPath := ExpandConstant('{tmp}\tfs-wait-for-executable-release.ps1');
  Script :=
    '$path = ''' + EscapePowerShellSingleQuoted(ExePath) + '''' + #13#10 +
    '$deadline = (Get-Date).AddSeconds(15)' + #13#10 +
    'do {' + #13#10 +
    '  try {' + #13#10 +
    '    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)' + #13#10 +
    '    $stream.Dispose()' + #13#10 +
    '    exit 0' + #13#10 +
    '  } catch {' + #13#10 +
    '    Start-Sleep -Milliseconds 250' + #13#10 +
    '  }' + #13#10 +
    '} while ((Get-Date) -lt $deadline)' + #13#10 +
    'exit 1' + #13#10;
  Result :=
    SaveStringToFile(ScriptPath, Script, False) and
    Exec(
      ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + ScriptPath + '"',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and
    (ResultCode = 0);
end;

function RequestSteamShutdown: Boolean;
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
    '  $processes = Get-Process steam,steamwebhelper,GameOverlayUI,steamerrorreporter -ErrorAction SilentlyContinue' + #13#10 +
    '} while ($processes -and (Get-Date) -lt $deadline)' + #13#10 +
    'if ($processes) {' + #13#10 +
    '  $processes | Stop-Process -Force -ErrorAction SilentlyContinue' + #13#10 +
    '  Start-Sleep -Seconds 2' + #13#10 +
    '}' + #13#10 +
    'if (Get-Process steam,steamwebhelper,GameOverlayUI,steamerrorreporter -ErrorAction SilentlyContinue) { exit 1 }' + #13#10 +
    'exit 0' + #13#10;

  Result :=
    SaveStringToFile(ScriptPath, Script, False) and
    Exec(
      ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + ScriptPath + '"',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and
    (ResultCode = 0);
end;

procedure ShowElevatedHelperInstallWarning(Message: string);
var
  GamepadLogPath: string;
begin
  if not WizardSilent then
  begin
    GamepadLogPath := ExpandConstant('{app}\data\gamepad-helper-task.log');
    MsgBox(
      Message + #13#10#13#10 +
      'You can still repair the elevated gamepad helper later after installation.' + #13#10#13#10 +
      'Details were written to:' + #13#10 + GamepadLogPath,
      mbInformation,
      MB_OK);
  end;
end;

function IsGamepadHelperTaskInstalled: Boolean;
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
    '--check-gamepad-helper-task',
    InstallRoot,
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) and (ResultCode = 0);
end;

procedure StartGamepadHelperAfterInstall;
var
  ResultCode: Integer;
begin
  if not IsGamepadHelperTaskInstalled then
  begin
    Log('The Gamepad Helper task is unavailable after installation; the runtime will keep using its local HID fallback.');
    Exit;
  end;

  if Exec(
      ExpandConstant('{sys}\schtasks.exe'),
      '/Run /TN "\ToolsForSteam\GamepadHelper"',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and (ResultCode = 0) then
  begin
    Log('The elevated Gamepad Helper task was started after installation.');
  end
  else
  begin
    Log('The post-install Gamepad Helper start returned code ' + IntToStr(ResultCode) + '; the runtime watchdog will retry it.');
  end;
end;

procedure RegisterElevatedHelperTaskSupport;
var
  ResultCode: Integer;
begin
  if not FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
  begin
    Log('Elevated helper task registration skipped because the executable is missing.');
    Exit;
  end;

  ForceDirectories(ExpandConstant('{app}\data'));

  if IsGamepadHelperTaskInstalled then
  begin
    Log('The elevated gamepad helper is already installed; refreshing it and sanitizing conflicting startup tasks.');
  end;

  UpdateSetupStatus('Installing the elevated gamepad helper...');

  if not Exec(
    ExpandConstant('{app}\{#MyAppExeName}'),
    '--register-installed-helper-tasks',
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    Log('Elevated helper task registration was skipped or cancelled.');
    ShowElevatedHelperInstallWarning('The elevated gamepad helper was not prepared during installation.');
    Exit;
  end;

  if not IsGamepadHelperTaskInstalled then
  begin
    Log('Elevated gamepad helper registration did not complete successfully.');
    ShowElevatedHelperInstallWarning('The elevated gamepad helper could not be prepared during installation.');
  end;
end;

function GetRtssInstalledVersion: string;
begin
  Result := '';
  RegQueryStringValue(
    HKLM32,
    'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RTSS',
    'DisplayVersion',
    Result);
end;

function TryGetRtssExecutablePathFromRegistry(RootKey: Integer; var ExecutablePath: string): Boolean;
var
  UninstallString: string;
  InstallLocation: string;
begin
  Result := False;
  ExecutablePath := '';
  InstallLocation := '';
  if RegQueryStringValue(
      RootKey,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RTSS',
      'InstallLocation',
      InstallLocation) and (InstallLocation <> '') then
  begin
    ExecutablePath := AddBackslash(InstallLocation) + 'RTSS.exe';
    if FileExists(ExecutablePath) then
    begin
      Result := True;
      Exit;
    end;
  end;

  UninstallString := '';
  if RegQueryStringValue(
      RootKey,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RTSS',
      'UninstallString',
      UninstallString) then
  begin
    RemoveQuotes(UninstallString);
    if ExtractFileDir(UninstallString) <> '' then
    begin
      ExecutablePath := AddBackslash(ExtractFileDir(UninstallString)) + 'RTSS.exe';
      Result := FileExists(ExecutablePath);
    end;
  end;
end;

function GetRtssExecutablePath: string;
begin
  if TryGetRtssExecutablePathFromRegistry(HKLM32, Result) then
    Exit;
  if TryGetRtssExecutablePathFromRegistry(HKLM64, Result) then
    Exit;
  if TryGetRtssExecutablePathFromRegistry(HKCU, Result) then
    Exit;

  Result := ExpandConstant('{pf32}\RivaTuner Statistics Server\RTSS.exe');
  if FileExists(Result) then
    Exit;
  Result := ExpandConstant('{pf64}\RivaTuner Statistics Server\RTSS.exe');
  if FileExists(Result) then
    Exit;
  Result := ExpandConstant('{localappdata}\Programs\RivaTuner Statistics Server\RTSS.exe');
  if not FileExists(Result) then
  begin
    Result := '';
  end;
end;

function IsRtssInstalled: Boolean;
begin
  { RTSS' package and executable versions can legitimately differ. The installed
    executable is the compatibility signal used by the TFS RTSS API bridge. }
  Result := FileExists(GetRtssExecutablePath);
end;

function StopRtssProcessesForMaintenance: Boolean;
var
  ScriptPath: string;
  Script: string;
  ResultCode: Integer;
begin
  ScriptPath := ExpandConstant('{tmp}\tools-for-steam-stop-rtss.ps1');
  Script :=
    '$ErrorActionPreference = "SilentlyContinue"' + #13#10 +
    '$names = @("RTSS", "EncoderServer", "EncoderServer64", "RTSSHooksLoader", "RTSSHooksLoader64", "DesktopOverlayHost", "DesktopOverlayHost64", "DesktopOverlayHostLoader")' + #13#10 +
    'Get-Process -Name $names | ForEach-Object { [void]$_.CloseMainWindow() }' + #13#10 +
    'Start-Sleep -Milliseconds 700' + #13#10 +
    'Get-Process -Name $names | Stop-Process -Force' + #13#10 +
    '$deadline = [DateTime]::UtcNow.AddSeconds(8)' + #13#10 +
    'while ((Get-Process -Name $names) -and [DateTime]::UtcNow -lt $deadline) {' + #13#10 +
    '  Start-Sleep -Milliseconds 200' + #13#10 +
    '}' + #13#10 +
    'if (Get-Process -Name $names) { exit 1 }' + #13#10 +
    'exit 0' + #13#10;

  Result :=
    SaveStringToFile(ScriptPath, Script, False) and
    Exec(
      ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + ScriptPath + '"',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and
    (ResultCode = 0);
  DeleteFile(ScriptPath);
end;

procedure InstallRtssIfNeeded;
var
  WingetPath: string;
  Parameters: string;
  ResultCode: Integer;
begin
  if IsRtssInstalled then
  begin
    Log('An existing RTSS installation was detected; its binaries and settings will be reused without reinstalling it.');
    Exit;
  end;

  UpdateSetupStatus('Closing RTSS background processes before installation...');
  if not StopRtssProcessesForMaintenance then
  begin
    RaiseException('Setup could not close all RTSS background processes before installation.');
  end;

  WingetPath := ExpandConstant('{localappdata}\Microsoft\WindowsApps\winget.exe');
  if not FileExists(WingetPath) then
  begin
    WingetPath := 'winget.exe';
  end;

  UpdateSetupStatus('Installing RivaTuner Statistics Server 7.3.7...');
  Parameters :=
    'install --id ' + RtssPackageId + ' --exact --version ' + RtssRequiredVersion +
    ' --source winget --scope machine --silent --accept-package-agreements' +
    ' --accept-source-agreements --disable-interactivity';

  if not Exec(WingetPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    RaiseException('Windows Package Manager could not be started. RTSS is a required Tools for Steam component.');
  end;

  if (ResultCode <> 0) or (not IsRtssInstalled) then
  begin
    RaiseException(
      'RTSS installation failed (winget code ' + IntToStr(ResultCode) + '). ' +
      'Setup was stopped because the performance overlay requires RTSS.');
  end;

  Log('RTSS ' + GetRtssInstalledVersion + ' was installed successfully.');
end;

procedure ConfigureRtssProfileAccess;
var
  RtssExecutablePath: string;
  ProfilesPath: string;
  ScriptPath: string;
  Script: string;
  ResultCode: Integer;
begin
  RtssExecutablePath := GetRtssExecutablePath;
  if RtssExecutablePath = '' then
  begin
    RaiseException('RTSS profile access could not be configured because RTSS.exe was not found.');
  end;

  ProfilesPath := AddBackslash(ExtractFileDir(RtssExecutablePath)) + 'Profiles';
  ScriptPath := ExpandConstant('{tmp}\tools-for-steam-configure-rtss-profiles.ps1');
  Script :=
    '$ErrorActionPreference = "Stop"' + #13#10 +
    '$profilesPath = ''' + EscapePowerShellSingleQuoted(ProfilesPath) + '''' + #13#10 +
    '$userSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User' + #13#10 +
    'if (-not (Test-Path -LiteralPath $profilesPath)) { New-Item -ItemType Directory -Path $profilesPath -Force | Out-Null }' + #13#10 +
    '$acl = Get-Acl -LiteralPath $profilesPath' + #13#10 +
    '$rights = [System.Security.AccessControl.FileSystemRights]::Modify' + #13#10 +
    '$inheritance = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit' + #13#10 +
    '$propagation = [System.Security.AccessControl.PropagationFlags]::None' + #13#10 +
    '$allow = [System.Security.AccessControl.AccessControlType]::Allow' + #13#10 +
    '$rule = [System.Security.AccessControl.FileSystemAccessRule]::new($userSid, $rights, $inheritance, $propagation, $allow)' + #13#10 +
    '$acl.SetAccessRule($rule)' + #13#10 +
    'Set-Acl -LiteralPath $profilesPath -AclObject $acl' + #13#10 +
    '$probe = Join-Path $profilesPath (''.tfs-write-probe-'' + [Guid]::NewGuid().ToString(''N'') + ''.tmp'')' + #13#10 +
    'try { [IO.File]::WriteAllText($probe, ''TFS''); Remove-Item -LiteralPath $probe -Force } finally { Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue }' + #13#10 +
    'exit 0' + #13#10;

  UpdateSetupStatus('Configuring secure RTSS game-profile access...');
  ResultCode := -1;
  if (not SaveStringToFile(ScriptPath, Script, False)) or
     (not Exec(
       ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       '-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + ScriptPath + '"',
       '',
       SW_HIDE,
       ewWaitUntilTerminated,
       ResultCode)) or
     (ResultCode <> 0) then
  begin
    DeleteFile(ScriptPath);
    RaiseException(
      'Setup could not grant the signed-in user access to RTSS game profiles. ' +
      'Installation was stopped because the frame limiter would not work reliably.');
  end;

  DeleteFile(ScriptPath);
  Log('Configured per-user write access for the RTSS Profiles folder.');
end;

function LegacyFpsHelperTaskExists: Boolean;
var
  ResultCode: Integer;
begin
  Result :=
    Exec(
      ExpandConstant('{sys}\schtasks.exe'),
      '/Query /TN "\ToolsForSteam\FpsHelper"',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and
    (ResultCode = 0);
end;

procedure RemoveLegacyFpsHelperTask;
var
  ResultCode: Integer;
begin
  if not LegacyFpsHelperTaskExists then
  begin
    Log('Legacy elevated FPS helper task is not present.');
    Exit;
  end;

  if not Exec(
    ExpandConstant('{sys}\schtasks.exe'),
    '/Delete /TN "\ToolsForSteam\FpsHelper" /F',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) or (ResultCode <> 0) then
  begin
    RaiseException(
      'Setup could not remove the obsolete elevated FPS helper task (schtasks code ' +
      IntToStr(ResultCode) + ').');
  end;

  if LegacyFpsHelperTaskExists then
  begin
    RaiseException('Setup removed the obsolete FPS helper task, but Windows still reports it as installed.');
  end;

  Log('Removed the obsolete elevated FPS helper task.');
end;

procedure SanitizeSteamAutostartSources;
var
  ResultCode: Integer;
begin
  if not FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
  begin
    Log('Steam autostart sanitation skipped because the executable is missing.');
    Exit;
  end;

  ResultCode := -1;
  if Exec(
      ExpandConstant('{app}\{#MyAppExeName}'),
      '--sanitize-steam-autostart',
      ExpandConstant('{app}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and (ResultCode = 0) then
  begin
    Log('Conflicting Steam and legacy TFS autostart sources were sanitized.');
  end
  else
  begin
    Log('Steam autostart sanitation returned code ' + IntToStr(ResultCode) + '.');
  end;
end;

function IsPawnIOCurrent: Boolean;
var
  InstalledVersion: string;
begin
  InstalledVersion := '';
  if not RegQueryStringValue(
      HKLM,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO',
      'DisplayVersion',
      InstalledVersion) then
  begin
    RegQueryStringValue(
      HKLM32,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO',
      'DisplayVersion',
      InstalledVersion);
  end;

  Result := (InstalledVersion <> '') and (CompareStr(InstalledVersion, '2.2.0.0') >= 0);
end;

function IsMsiClawA8: Boolean;
var
  Manufacturer: string;
  ProductName: string;
begin
  Manufacturer := '';
  ProductName := '';
  RegQueryStringValue(HKLM, 'HARDWARE\DESCRIPTION\System\BIOS', 'BaseBoardManufacturer', Manufacturer);
  RegQueryStringValue(HKLM, 'HARDWARE\DESCRIPTION\System\BIOS', 'BaseBoardProduct', ProductName);
  Result :=
    (CompareText(Manufacturer, 'MICRO-STAR INTERNATIONAL CO., LTD.') = 0) and
    (CompareText(ProductName, 'MS-1T8K') = 0);
end;

{ True for a token like 7840U, 6800HS or 4800H (four digits plus U, H or HS; HX is a desktop-class part). }
function IsMobileModelToken(Token: string): Boolean;
var
  Index: Integer;
  Suffix: string;
begin
  Result := False;
  if Length(Token) < 5 then
    Exit;
  for Index := 1 to 4 do
    if (Token[Index] < '0') or (Token[Index] > '9') then
      Exit;
  Suffix := Copy(Token, 5, Length(Token) - 4);
  Result := (Suffix = 'U') or (Suffix = 'H') or (Suffix = 'HS');
end;

{ Mirrors AmdTdpCatalog: AMD APUs whose TDP Tools for Steam can control through PawnIO. }
function IsAmdTdpProcessor: Boolean;
var
  CpuName: string;
  Token: string;
  SpaceIndex: Integer;
begin
  Result := False;
  CpuName := '';
  RegQueryStringValue(HKLM, 'HARDWARE\DESCRIPTION\System\CentralProcessor\0', 'ProcessorNameString', CpuName);
  CpuName := Uppercase(Trim(CpuName));
  if Pos('AMD', CpuName) = 0 then
    Exit;

  if (Pos('RYZEN AI', CpuName) > 0) or (Pos('RYZEN Z1', CpuName) > 0) or (Pos('RYZEN Z2', CpuName) > 0) or
     (Pos('CUSTOM APU 0405', CpuName) > 0) or (Pos('CUSTOM APU 0932', CpuName) > 0) then
  begin
    Result := True;
    Exit;
  end;

  CpuName := CpuName + ' ';
  while CpuName <> '' do
  begin
    SpaceIndex := Pos(' ', CpuName);
    Token := Copy(CpuName, 1, SpaceIndex - 1);
    Delete(CpuName, 1, SpaceIndex);
    if IsMobileModelToken(Token) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function SuspendHandheldReplacementForInstall: Boolean;
var
  UpdateHelperPath: string;
  DataDirectory: string;
  ReplacementStatePath: string;
  ResultCode: Integer;
begin
  Result := True;
  if not IsMsiClawA8 then
    Exit;

  DataDirectory := ExpandConstant('{app}\data');
  ReplacementStatePath := ExpandConstant('{app}\data\handheld-replacement-state.json');
  if not FileExists(ReplacementStatePath) then
    Exit;

  UpdateSetupStatus('Safely suspending the handheld controller replacement for the update...');
  ExtractTemporaryFile('ToolsForSteamUpdateHelper.exe');
  UpdateHelperPath := ExpandConstant('{tmp}\ToolsForSteamUpdateHelper.exe');
  Result := Exec(
      UpdateHelperPath,
      '--suspend-handheld-replacement-for-update --handheld-data-directory="' + DataDirectory + '"',
      ExpandConstant('{tmp}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and (ResultCode = 0);
end;

function IsUsbIpWin2Installed: Boolean;
begin
  Result :=
    RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\usbip2_ude') or
    RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\usbip2_stub');
end;

function IsHidHideInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\HidHide');
end;

procedure InstallMandatoryHandheldReplacement;
var
  UsbIpSetupPath: string;
  HidHideSetupPath: string;
  PrepareArguments: string;
  ResultCode: Integer;
  RestoreResultCode: Integer;
  UsbIpOwnedByTfs: Boolean;
  HidHideOwnedByTfs: Boolean;
begin
  if not IsMsiClawA8 then
  begin
    Log('Mandatory handheld replacement skipped because this is not a supported MSI Claw A8.');
    Exit;
  end;

  UsbIpSetupPath := ExpandConstant('{app}\ThirdParty\HandheldDrivers\USBip-0.9.7.8-x64.exe');
  HidHideSetupPath := ExpandConstant('{app}\ThirdParty\HandheldDrivers\HidHide_1.5.230_x64.exe');
  if not FileExists(ExpandConstant('{app}\ThirdParty\VIIPER\viiper.exe')) or
     not FileExists(UsbIpSetupPath) or not FileExists(HidHideSetupPath) then
  begin
    RaiseException('The mandatory handheld controller runtime is incomplete.');
  end;

  if LowerCase(GetSHA256OfFile(UsbIpSetupPath)) <> UsbIpSetupSha256 then
    RaiseException('USBIP-Win2 failed SHA-256 verification.');
  if LowerCase(GetSHA256OfFile(HidHideSetupPath)) <> HidHideSetupSha256 then
    RaiseException('HidHide failed SHA-256 verification.');

  UsbIpOwnedByTfs := not IsUsbIpWin2Installed;
  if UsbIpOwnedByTfs then
  begin
    UpdateSetupStatus('Installing the signed USBIP controller driver for this handheld...');
    if not Exec(UsbIpSetupPath, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART',
        ExtractFileDir(UsbIpSetupPath), SW_HIDE, ewWaitUntilTerminated, ResultCode) or
       ((ResultCode <> 0) and (ResultCode <> 3010)) then
      RaiseException('The mandatory USBIP-Win2 driver could not be installed.');
    HandheldDriversInstalledThisRun := True;
  end;

  HidHideOwnedByTfs := not IsHidHideInstalled;
  if HidHideOwnedByTfs then
  begin
    UpdateSetupStatus('Installing the signed HidHide input isolation driver for this handheld...');
    if not Exec(HidHideSetupPath, '/qn /norestart',
        ExtractFileDir(HidHideSetupPath), SW_HIDE, ewWaitUntilTerminated, ResultCode) or
       ((ResultCode <> 0) and (ResultCode <> 3010)) then
      RaiseException('The mandatory HidHide driver could not be installed.');
    HandheldDriversInstalledThisRun := True;
  end;

  UpdateSetupStatus('Disabling MSI Center M services and autostarts for the TFS replacement...');
  if not Exec(
      ExpandConstant('{app}\{#MyAppExeName}'),
      '--prepare-handheld-oem',
      ExpandConstant('{app}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) or (ResultCode <> 0) then
  begin
    Exec(
      ExpandConstant('{app}\{#MyAppExeName}'),
      '--restore-handheld-replacement',
      ExpandConstant('{app}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      RestoreResultCode);
    RaiseException('MSI Center M could not be prepared for the mandatory TFS controller replacement.');
  end;

  PrepareArguments := '--prepare-handheld-replacement';
  if UsbIpOwnedByTfs then
    PrepareArguments := PrepareArguments + ' --usbip-owned-by-tfs';
  if HidHideOwnedByTfs then
    PrepareArguments := PrepareArguments + ' --hidhide-owned-by-tfs';

  UpdateSetupStatus('Preparing the mandatory TFS controller replacement...');
  if not Exec(ExpandConstant('{app}\{#MyAppExeName}'), PrepareArguments,
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) or
     (ResultCode <> 0) then
    RaiseException('Tools for Steam could not prepare the mandatory handheld replacement.');

  Log('Mandatory VIIPER handheld replacement was prepared successfully.');
end;

procedure InstallPawnIOIfNeeded;
var
  SetupPath: string;
  ActualHash: string;
  ResultCode: Integer;
begin
  if not (IsMsiClawA8 or IsAmdTdpProcessor) then
  begin
    Log('PawnIO installation skipped because this device has no AMD APU with TDP control.');
    Exit;
  end;

  if IsPawnIOCurrent then
  begin
    Log('PawnIO 2.2.0 or newer is already installed.');
    Exit;
  end;

  SetupPath := ExpandConstant('{app}\ThirdParty\PawnIO\PawnIO_setup.exe');
  if not FileExists(SetupPath) then
  begin
    Log('PawnIO setup is missing from the installed payload.');
    Exit;
  end;

  ActualHash := LowerCase(GetSHA256OfFile(SetupPath));
  if ActualHash <> PawnIOSetupSha256 then
  begin
    Log('PawnIO setup hash verification failed.');
    if not WizardSilent then
      MsgBox('PawnIO was not installed because its setup file failed verification.', mbError, MB_OK);
    Exit;
  end;

  UpdateSetupStatus('Installing PawnIO 2.2.0 for handheld TDP control...');
  if not Exec(
      SetupPath,
      '-install -silent',
      ExpandConstant('{app}\ThirdParty\PawnIO'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
  begin
    Log('PawnIO installation was cancelled or could not be started.');
    if not WizardSilent then
      MsgBox('PawnIO installation was cancelled. You can install or repair it later from the handheld performance plugin.', mbInformation, MB_OK);
    Exit;
  end;

  if (ResultCode <> 0) and (ResultCode <> 3010) then
  begin
    Log('PawnIO setup returned exit code ' + IntToStr(ResultCode) + '.');
    if not WizardSilent then
      MsgBox('PawnIO setup did not finish successfully. You can retry it later from the handheld performance plugin.', mbInformation, MB_OK);
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
      { The removed Shell Takeover ("shell") and Xbox Mode ("xbox"/"external")
        fall through to Console Mode, which replaces them as the console start. }
      if Pos('"console"', SettingsJsonLower) > 0 then
      begin
        Result := 'console';
      end
      else if Pos('"tray"', SettingsJsonLower) > 0 then
      begin
        Result := 'tray';
      end
      else
      begin
        Result := 'console';
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
    { A leftover Shell Takeover value (removed): migrate it to Console Mode. }
    if (ContainsText(ShellValue, 'ToolsForSteam.exe') or ContainsText(ShellValue, 'SteamLoader.exe')) and ContainsText(ShellValue, '--shell-bootstrap') then
    begin
      Result := 'console';
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

function GetDeviceDisplayName: string;
var
  Manufacturer: string;
  ProductName: string;
begin
  Manufacturer := Trim(GetRegistryStringValue(HKLM, 'HARDWARE\DESCRIPTION\System\BIOS', 'SystemManufacturer'));
  ProductName := Trim(GetRegistryStringValue(HKLM, 'HARDWARE\DESCRIPTION\System\BIOS', 'SystemProductName'));
  if ProductName = '' then
    Result := Manufacturer
  else if (Manufacturer = '') or (Pos(Uppercase(Manufacturer), Uppercase(ProductName)) = 1) then
    Result := ProductName
  else
    Result := Manufacturer + ' ' + ProductName;
  if Result = '' then
    Result := 'This PC';
end;

{ What the handheld-specific part of setup will do on this device. Mirrors
  InstallPawnIOIfNeeded and InstallMandatoryHandheldReplacement. }
function DescribeHandheldSupport: string;
begin
  if IsMsiClawA8 then
    Result := FmtMessage(CustomMessage('TfsHandheldClaw'), ['MSI Claw A8'])
  else if IsAmdTdpProcessor then
    Result := FmtMessage(
      CustomMessage('TfsHandheldAmd'), [
        GetDeviceDisplayName,
        Trim(GetRegistryStringValue(HKLM, 'HARDWARE\DESCRIPTION\System\CentralProcessor\0', 'ProcessorNameString'))])
  else
    Result := CustomMessage('TfsHandheldNone');
end;

procedure UpdateSystemCheckPage;
var
  CurrentMode: string;
begin
  CurrentMode := ExistingStartupMode;
  if CurrentMode = '' then
  begin
    CurrentMode := 'clean install';
  end;

  SystemCheckSummaryLabel.Caption := CustomMessage('TfsSystemCheckSummary');
  SystemCheckWindowsLabel.Caption :=
    CustomMessage('TfsStatusReady') + '   ' +
    FmtMessage(
      CustomMessage('TfsWindowsReady'), [
        IntToStr(GetWindowsBuildNumber()), IntToStr(GetWindowsBuildRevision())]);

  if SteamInstallPath <> '' then
  begin
    SystemCheckSteamLabel.Caption :=
      CustomMessage('TfsStatusReady') + '   ' +
      FmtMessage(CustomMessage('TfsSteamFound'), [SteamInstallPath]);
  end
  else
  begin
    SystemCheckSteamLabel.Caption :=
      CustomMessage('TfsStatusNotice') + '   ' +
      CustomMessage('TfsSteamMissing');
  end;

  if ExistingInstallPath <> '' then
  begin
    SystemCheckInstallLabel.Caption :=
      CustomMessage('TfsStatusReady') + '   ' +
      FmtMessage(
        CustomMessage('TfsInstallFound'), [
          ExistingInstallPath, DescribeStartupMode(CurrentMode)]);
  end
  else
  begin
    SystemCheckInstallLabel.Caption :=
      CustomMessage('TfsStatusReady') + '   ' +
      CustomMessage('TfsInstallFresh');
  end;

  SystemCheckHandheldLabel.Caption :=
    CustomMessage('TfsStatusReady') + '   ' + DescribeHandheldSupport;
end;

procedure SystemCheckDetailsButtonClick(Sender: TObject);
begin
  MsgBox(
    CustomMessage('TfsHelperDetails') + #13#10#13#10 +
    CustomMessage('TfsPerformanceDetails') + #13#10#13#10 +
    CustomMessage('TfsWebView2Details') + #13#10#13#10 +
    CustomMessage('TfsRollbackDetails'),
    mbInformation,
    MB_OK);
end;

procedure UpdateStartupModePageState;
begin
  ConsoleModeRadio.Enabled := True;
  TrayModeRadio.Enabled := True;
end;

procedure FitWrappedLabel(ALabel: TNewStaticText; MinimumHeight: Integer);
begin
  ALabel.Height := MinimumHeight;
  WizardForm.AdjustLabelHeight(ALabel);
  if ALabel.Height < MinimumHeight then
  begin
    ALabel.Height := MinimumHeight;
  end;
end;

procedure LayoutSystemCheckPage;
var
  ContentWidth: Integer;
  CardTop: Integer;
  CardGap: Integer;
  CardPaddingX: Integer;
  CardPaddingY: Integer;
begin
  ContentWidth := SystemCheckPage.SurfaceWidth;
  CardGap := ScaleY(6);
  CardPaddingX := ScaleX(14);
  CardPaddingY := ScaleY(8);

  SystemCheckSummaryLabel.Width := ContentWidth - ScaleX(4);
  FitWrappedLabel(SystemCheckSummaryLabel, ScaleY(18));
  CardTop :=
    SystemCheckSummaryLabel.Top +
    SystemCheckSummaryLabel.Height +
    ScaleY(10);

  SystemCheckWindowsPanel.Top := CardTop;
  SystemCheckWindowsPanel.Width := ContentWidth;
  SystemCheckWindowsLabel.Left := CardPaddingX;
  SystemCheckWindowsLabel.Top := CardTop + CardPaddingY;
  SystemCheckWindowsLabel.Width := ContentWidth - (CardPaddingX * 2);
  FitWrappedLabel(SystemCheckWindowsLabel, ScaleY(20));
  SystemCheckWindowsPanel.Height :=
    SystemCheckWindowsLabel.Height + (CardPaddingY * 2);
  CardTop :=
    SystemCheckWindowsPanel.Top +
    SystemCheckWindowsPanel.Height +
    CardGap;

  SystemCheckSteamPanel.Top := CardTop;
  SystemCheckSteamPanel.Width := ContentWidth;
  SystemCheckSteamLabel.Left := CardPaddingX;
  SystemCheckSteamLabel.Top := CardTop + CardPaddingY;
  SystemCheckSteamLabel.Width := ContentWidth - (CardPaddingX * 2);
  FitWrappedLabel(SystemCheckSteamLabel, ScaleY(20));
  SystemCheckSteamPanel.Height :=
    SystemCheckSteamLabel.Height + (CardPaddingY * 2);
  CardTop :=
    SystemCheckSteamPanel.Top +
    SystemCheckSteamPanel.Height +
    CardGap;

  SystemCheckInstallPanel.Top := CardTop;
  SystemCheckInstallPanel.Width := ContentWidth;
  SystemCheckInstallLabel.Left := CardPaddingX;
  SystemCheckInstallLabel.Top := CardTop + CardPaddingY;
  SystemCheckInstallLabel.Width := ContentWidth - (CardPaddingX * 2);
  FitWrappedLabel(SystemCheckInstallLabel, ScaleY(20));
  SystemCheckInstallPanel.Height :=
    SystemCheckInstallLabel.Height + (CardPaddingY * 2);
  CardTop :=
    SystemCheckInstallPanel.Top +
    SystemCheckInstallPanel.Height +
    CardGap;

  SystemCheckHandheldPanel.Top := CardTop;
  SystemCheckHandheldPanel.Width := ContentWidth;
  SystemCheckHandheldLabel.Left := CardPaddingX;
  SystemCheckHandheldLabel.Top := CardTop + CardPaddingY;
  SystemCheckHandheldLabel.Width := ContentWidth - (CardPaddingX * 2);
  FitWrappedLabel(SystemCheckHandheldLabel, ScaleY(20));
  SystemCheckHandheldPanel.Height :=
    SystemCheckHandheldLabel.Height + (CardPaddingY * 2);

  SystemCheckDetailsButton.Top :=
    SystemCheckHandheldPanel.Top +
    SystemCheckHandheldPanel.Height +
    ScaleY(10);
  SystemCheckDetailsButton.Width :=
    WizardForm.CalculateButtonWidth([SystemCheckDetailsButton.Caption]);
end;

procedure LayoutStartupModeCard(
  Panel: TPanel;
  Radio: TNewRadioButton;
  DescriptionLabel: TNewStaticText;
  ContentWidth: Integer;
  var CardTop: Integer);
var
  CardPaddingX: Integer;
  CardPaddingY: Integer;
begin
  CardPaddingX := ScaleX(14);
  CardPaddingY := ScaleY(8);

  Panel.Top := CardTop;
  Panel.Width := ContentWidth;
  Radio.Left := CardPaddingX;
  Radio.Top := CardTop + CardPaddingY;
  Radio.Width := ContentWidth - (CardPaddingX * 2);
  DescriptionLabel.Left := CardPaddingX + ScaleX(22);
  DescriptionLabel.Top := Radio.Top + Radio.Height + ScaleY(4);
  DescriptionLabel.Width := ContentWidth - DescriptionLabel.Left - CardPaddingX;
  FitWrappedLabel(DescriptionLabel, ScaleY(18));
  Panel.Height :=
    DescriptionLabel.Top +
    DescriptionLabel.Height +
    CardPaddingY -
    Panel.Top;
  CardTop := Panel.Top + Panel.Height + ScaleY(6);
end;

procedure LayoutStartupModePage;
var
  ContentWidth: Integer;
  CardTop: Integer;
begin
  ContentWidth := StartupModePage.SurfaceWidth;

  StartupModeSummaryLabel.Width := ContentWidth - ScaleX(4);
  FitWrappedLabel(StartupModeSummaryLabel, ScaleY(18));
  CardTop :=
    StartupModeSummaryLabel.Top +
    StartupModeSummaryLabel.Height +
    ScaleY(8);

  LayoutStartupModeCard(ConsoleModePanel, ConsoleModeRadio, ConsoleModeDescriptionLabel, ContentWidth, CardTop);
  LayoutStartupModeCard(TrayModePanel, TrayModeRadio, TrayModeDescriptionLabel, ContentWidth, CardTop);
end;

procedure StartupModeSelectionChanged(Sender: TObject);
begin
  UpdateStartupModePageState;
  LayoutStartupModePage;
end;

function SelectedStartupMode: string;
begin
#if VariantCustomPages == "0"
  if UpdateStartupModeToRestore = 'tray' then
  begin
    Result := 'tray';
  end
  else
  begin
    Result := 'console';
  end;
#else
  if WizardSilent then
  begin
    if UpdateInstallRequested and (UpdateStartupModeToRestore <> '') then
    begin
      Result := UpdateStartupModeToRestore;
    end
    else
    begin
      Result := ExistingStartupMode;
    end;
    { Only Console Mode and eTray exist; anything else (removed Shell
      Takeover, Xbox Mode, nothing yet) becomes Console Mode. }
    if Result <> 'tray' then
    begin
      Result := 'console';
    end;
  end
  else if TrayModeRadio.Checked then
  begin
    Result := 'tray';
  end
  else
  begin
    Result := 'console';
  end;
#endif
end;

procedure InitializeWizard;
var
  CurrentMode: string;
begin
  UpdateInstallRequested := CompareText(ExpandConstant('{param:TFSUPDATE|0}'), '1') = 0;
#if VariantCustomPages == "0"
  ExistingInstallPath := GetExistingInstallLocation;
  SteamInstallPath := GetSteamInstallLocation;
  UpdateStartupModeToRestore := ExistingStartupMode;
#else
  CurrentMode := ExistingStartupMode;
  ExistingInstallPath := GetExistingInstallLocation;
  SteamInstallPath := GetSteamInstallLocation;
  UpdateStartupModeToRestore := CurrentMode;
#if VariantFreshInstallPreview == "1"
  CurrentMode := '';
  ExistingInstallPath := '';
  UpdateStartupModeToRestore := '';
#endif

  SystemCheckPage := CreateCustomPage(
    wpWelcome,
    CustomMessage('TfsSystemCheckCaption'),
    CustomMessage('TfsSystemCheckDescription'));

  SystemCheckSummaryLabel := TNewStaticText.Create(WizardForm);
  SystemCheckSummaryLabel.Parent := SystemCheckPage.Surface;
  SystemCheckSummaryLabel.Left := ScaleX(2);
  SystemCheckSummaryLabel.Top := ScaleY(4);
  SystemCheckSummaryLabel.Width := SystemCheckPage.SurfaceWidth - ScaleX(4);
  SystemCheckSummaryLabel.Height := ScaleY(36);
  SystemCheckSummaryLabel.AutoSize := False;
  SystemCheckSummaryLabel.WordWrap := True;

  SystemCheckWindowsPanel := TPanel.Create(WizardForm);
  SystemCheckWindowsPanel.Parent := SystemCheckPage.Surface;
  SystemCheckWindowsPanel.Left := 0;
  SystemCheckWindowsPanel.Top := ScaleY(56);
  SystemCheckWindowsPanel.Width := SystemCheckPage.SurfaceWidth;
  SystemCheckWindowsPanel.Height := ScaleY(54);
  SystemCheckWindowsPanel.Caption := '';
  SystemCheckWindowsPanel.BevelOuter := bvLowered;

  SystemCheckWindowsLabel := TNewStaticText.Create(WizardForm);
  SystemCheckWindowsLabel.Parent := SystemCheckPage.Surface;
  SystemCheckWindowsLabel.Left := ScaleX(14);
  SystemCheckWindowsLabel.Top := ScaleY(66);
  SystemCheckWindowsLabel.Width := SystemCheckPage.SurfaceWidth - ScaleX(28);
  SystemCheckWindowsLabel.Height := ScaleY(38);
  SystemCheckWindowsLabel.AutoSize := False;
  SystemCheckWindowsLabel.WordWrap := True;
  SystemCheckWindowsLabel.Font.Style := [fsBold];

  SystemCheckSteamPanel := TPanel.Create(WizardForm);
  SystemCheckSteamPanel.Parent := SystemCheckPage.Surface;
  SystemCheckSteamPanel.Left := 0;
  SystemCheckSteamPanel.Top := ScaleY(116);
  SystemCheckSteamPanel.Width := SystemCheckPage.SurfaceWidth;
  SystemCheckSteamPanel.Height := ScaleY(54);
  SystemCheckSteamPanel.Caption := '';
  SystemCheckSteamPanel.BevelOuter := bvLowered;

  SystemCheckSteamLabel := TNewStaticText.Create(WizardForm);
  SystemCheckSteamLabel.Parent := SystemCheckPage.Surface;
  SystemCheckSteamLabel.Left := ScaleX(14);
  SystemCheckSteamLabel.Top := ScaleY(126);
  SystemCheckSteamLabel.Width := SystemCheckPage.SurfaceWidth - ScaleX(28);
  SystemCheckSteamLabel.Height := ScaleY(38);
  SystemCheckSteamLabel.AutoSize := False;
  SystemCheckSteamLabel.WordWrap := True;
  SystemCheckSteamLabel.Font.Style := [fsBold];

  SystemCheckInstallPanel := TPanel.Create(WizardForm);
  SystemCheckInstallPanel.Parent := SystemCheckPage.Surface;
  SystemCheckInstallPanel.Left := 0;
  SystemCheckInstallPanel.Top := ScaleY(176);
  SystemCheckInstallPanel.Width := SystemCheckPage.SurfaceWidth;
  SystemCheckInstallPanel.Height := ScaleY(62);
  SystemCheckInstallPanel.Caption := '';
  SystemCheckInstallPanel.BevelOuter := bvLowered;

  SystemCheckInstallLabel := TNewStaticText.Create(WizardForm);
  SystemCheckInstallLabel.Parent := SystemCheckPage.Surface;
  SystemCheckInstallLabel.Left := ScaleX(14);
  SystemCheckInstallLabel.Top := ScaleY(186);
  SystemCheckInstallLabel.Width := SystemCheckPage.SurfaceWidth - ScaleX(28);
  SystemCheckInstallLabel.Height := ScaleY(46);
  SystemCheckInstallLabel.AutoSize := False;
  SystemCheckInstallLabel.WordWrap := True;
  SystemCheckInstallLabel.Font.Style := [fsBold];

  SystemCheckHandheldPanel := TPanel.Create(WizardForm);
  SystemCheckHandheldPanel.Parent := SystemCheckPage.Surface;
  SystemCheckHandheldPanel.Left := 0;
  SystemCheckHandheldPanel.Top := ScaleY(244);
  SystemCheckHandheldPanel.Width := SystemCheckPage.SurfaceWidth;
  SystemCheckHandheldPanel.Height := ScaleY(62);
  SystemCheckHandheldPanel.Caption := '';
  SystemCheckHandheldPanel.BevelOuter := bvLowered;

  SystemCheckHandheldLabel := TNewStaticText.Create(WizardForm);
  SystemCheckHandheldLabel.Parent := SystemCheckPage.Surface;
  SystemCheckHandheldLabel.Left := ScaleX(14);
  SystemCheckHandheldLabel.Top := ScaleY(254);
  SystemCheckHandheldLabel.Width := SystemCheckPage.SurfaceWidth - ScaleX(28);
  SystemCheckHandheldLabel.Height := ScaleY(46);
  SystemCheckHandheldLabel.AutoSize := False;
  SystemCheckHandheldLabel.WordWrap := True;
  SystemCheckHandheldLabel.Font.Style := [fsBold];

  SystemCheckDetailsButton := TNewButton.Create(WizardForm);
  SystemCheckDetailsButton.Parent := SystemCheckPage.Surface;
  SystemCheckDetailsButton.Left := 0;
  SystemCheckDetailsButton.Top := ScaleY(248);
  SystemCheckDetailsButton.Width := ScaleX(164);
  SystemCheckDetailsButton.Height := ScaleY(28);
  SystemCheckDetailsButton.Caption := CustomMessage('TfsShowDetails');
  SystemCheckDetailsButton.OnClick := @SystemCheckDetailsButtonClick;

  StartupModePage := CreateCustomPage(
    SystemCheckPage.ID,
    CustomMessage('TfsModeCaption'),
    CustomMessage('TfsModeDescription'));

  StartupModeSummaryLabel := TNewStaticText.Create(WizardForm);
  StartupModeSummaryLabel.Parent := StartupModePage.Surface;
  StartupModeSummaryLabel.Left := ScaleX(2);
  StartupModeSummaryLabel.Top := ScaleY(4);
  StartupModeSummaryLabel.Width := StartupModePage.SurfaceWidth - ScaleX(4);
  StartupModeSummaryLabel.Height := ScaleY(36);
  StartupModeSummaryLabel.AutoSize := False;
  StartupModeSummaryLabel.WordWrap := True;
  StartupModeSummaryLabel.Caption := CustomMessage('TfsModeSummary');

  { Positions below are placeholders; LayoutStartupModePage stacks the cards. }
  ConsoleModePanel := TPanel.Create(WizardForm);
  ConsoleModePanel.Parent := StartupModePage.Surface;
  ConsoleModePanel.Left := 0;
  ConsoleModePanel.Top := ScaleY(48);
  ConsoleModePanel.Width := StartupModePage.SurfaceWidth;
  ConsoleModePanel.Height := ScaleY(70);
  ConsoleModePanel.Caption := '';
  ConsoleModePanel.BevelOuter := bvLowered;

  ConsoleModeRadio := TNewRadioButton.Create(WizardForm);
  ConsoleModeRadio.Parent := StartupModePage.Surface;
  ConsoleModeRadio.Left := ScaleX(14);
  ConsoleModeRadio.Top := ScaleY(58);
  ConsoleModeRadio.Width := StartupModePage.SurfaceWidth - ScaleX(28);
  ConsoleModeRadio.Caption := CustomMessage('TfsConsoleTitle');
  ConsoleModeRadio.Checked := (CurrentMode = '') or (CurrentMode = 'console');
  ConsoleModeRadio.OnClick := @StartupModeSelectionChanged;
  ConsoleModeRadio.Font.Style := [fsBold];

  ConsoleModeDescriptionLabel := TNewStaticText.Create(WizardForm);
  ConsoleModeDescriptionLabel.Parent := StartupModePage.Surface;
  ConsoleModeDescriptionLabel.Left := ScaleX(36);
  ConsoleModeDescriptionLabel.Top := ScaleY(82);
  ConsoleModeDescriptionLabel.Width := StartupModePage.SurfaceWidth - ScaleX(50);
  ConsoleModeDescriptionLabel.Height := ScaleY(30);
  ConsoleModeDescriptionLabel.AutoSize := False;
  ConsoleModeDescriptionLabel.WordWrap := True;
  ConsoleModeDescriptionLabel.Caption := CustomMessage('TfsConsoleDescription');

  TrayModePanel := TPanel.Create(WizardForm);
  TrayModePanel.Parent := StartupModePage.Surface;
  TrayModePanel.Left := 0;
  TrayModePanel.Top := ScaleY(124);
  TrayModePanel.Width := StartupModePage.SurfaceWidth;
  TrayModePanel.Height := ScaleY(70);
  TrayModePanel.Caption := '';
  TrayModePanel.BevelOuter := bvLowered;

  TrayModeRadio := TNewRadioButton.Create(WizardForm);
  TrayModeRadio.Parent := StartupModePage.Surface;
  TrayModeRadio.Left := ScaleX(14);
  TrayModeRadio.Top := ScaleY(134);
  TrayModeRadio.Width := StartupModePage.SurfaceWidth - ScaleX(28);
  TrayModeRadio.Caption := CustomMessage('TfsTrayTitle');
  TrayModeRadio.Checked := CurrentMode = 'tray';
  TrayModeRadio.OnClick := @StartupModeSelectionChanged;
  TrayModeRadio.Font.Style := [fsBold];

  TrayModeDescriptionLabel := TNewStaticText.Create(WizardForm);
  TrayModeDescriptionLabel.Parent := StartupModePage.Surface;
  TrayModeDescriptionLabel.Left := ScaleX(36);
  TrayModeDescriptionLabel.Top := ScaleY(158);
  TrayModeDescriptionLabel.Width := StartupModePage.SurfaceWidth - ScaleX(50);
  TrayModeDescriptionLabel.Height := ScaleY(30);
  TrayModeDescriptionLabel.AutoSize := False;
  TrayModeDescriptionLabel.WordWrap := True;
  TrayModeDescriptionLabel.Caption := CustomMessage('TfsTrayDescription');

  if not (ConsoleModeRadio.Checked or TrayModeRadio.Checked) then
  begin
    ConsoleModeRadio.Checked := True;
  end;

  UpdateSystemCheckPage;
  LayoutSystemCheckPage;
  UpdateStartupModePageState;
  LayoutStartupModePage;
#endif

  InitialInstallWasUpdate :=
    UpdateInstallRequested or (ExistingInstallPath <> '');
  WizardForm.NextButton.Width :=
    WizardForm.CalculateButtonWidth([CustomMessage('TfsContinue'),
      CustomMessage('TfsInstallNow'),
      CustomMessage('TfsUpdateNow')]);
  WizardForm.NextButton.Left :=
    WizardForm.CancelButton.Left - WizardForm.NextButton.Width - ScaleX(8);
  WizardForm.BackButton.Left :=
    WizardForm.NextButton.Left - WizardForm.BackButton.Width - ScaleX(8);
  WizardForm.StatusLabel.Font.Style := [fsBold];
end;

procedure LeaveLegacyXboxModeBeforeInstall;
var
  GamingHomeApp: string;
  InstallLocation: string;
  ExistingExe: string;
  ResultCode: Integer;
begin
  { Older installs could register TFS as the Windows Xbox Mode Gaming Home app.
    Windows would restart that home app, and with it the old TFS, while files
    are being replaced. Hand the Gaming Home back to Windows first with the
    installed runtime, which still knows the old rollback snapshot. }
  GamingHomeApp := GetRegistryStringValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\GamingConfiguration',
    'GamingHomeApp');
  if CompareText(GamingHomeApp, 'GCM.ToolsForSteam.XboxHost_kpg9gzy2ksp2j!App') <> 0 then
  begin
    Exit;
  end;

  InstallLocation := GetExistingInstallLocation;
  if InstallLocation = '' then
  begin
    Exit;
  end;

  ExistingExe := AddBackslash(InstallLocation) + '{#MyAppExeName}';
  if not FileExists(ExistingExe) then
  begin
    Exit;
  end;

  UpdateSetupStatus('Leaving the retired Xbox Mode before updating files...');
  Exec(ExistingExe, '--restore-xbox-mode', InstallLocation, SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log('Legacy Xbox Mode hand-back before install returned code ' + IntToStr(ResultCode) + '.');
end;

procedure ResumeAfterFailedInstall;
begin
  ResumeElevatedHelperTasks;
end;

function ApplyInstalledStartupMode(Mode: string): Boolean;
var
  ResultCode: Integer;
begin
  { The shell value, Run value and Console Mode sign-in task belong to the
    signed-in user, not to the account that approved the UAC prompt. }
  Result :=
    FileExists(ExpandConstant('{app}\{#MyAppExeName}')) and
    ExecAsOriginalUser(
      ExpandConstant('{app}\{#MyAppExeName}'),
      '--set-startup-mode=' + Mode,
      ExpandConstant('{app}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and
    (ResultCode = 0);
  if not Result then
  begin
    Log('The installed TFS runtime did not confirm startup mode ' + Mode + '.');
  end;
end;

function FinalizeInstalledStartupMode(AllowTrayFallback: Boolean): Boolean;
var
  TargetMode: string;
begin
  TargetMode := SelectedStartupMode;
  Result := ApplyInstalledStartupMode(TargetMode);
  if not Result and AllowTrayFallback and (TargetMode <> 'tray') then
  begin
    TargetMode := 'tray';
    Result := ApplyInstalledStartupMode(TargetMode);
  end;

  if Result then
  begin
    FinalizedStartupMode := TargetMode;
    StartupModeFinalized := True;
  end;
end;

function IsTrayMode: Boolean;
begin
  Result := SelectedStartupMode = 'tray';
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

function NextButtonClick(CurPageID: Integer): Boolean;
begin
#if VariantUiPreview == "1"
  if CurPageID = wpReady then
  begin
    MsgBox(CustomMessage('TfsPreviewStop'), mbInformation, MB_OK);
    Result := False;
    Exit;
  end;
#endif
  Result := True;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpWelcome then
  begin
    if InitialInstallWasUpdate then
    begin
      WizardForm.WelcomeLabel1.Caption := CustomMessage('TfsUpdateWelcomeTitle');
      WizardForm.WelcomeLabel2.Caption := CustomMessage('TfsUpdateWelcomeBody');
    end
    else
    begin
      WizardForm.WelcomeLabel1.Caption := CustomMessage('TfsWelcomeTitle');
      WizardForm.WelcomeLabel2.Caption := CustomMessage('TfsWelcomeBody');
    end;
    WizardForm.NextButton.Caption := CustomMessage('TfsContinue');
  end;

#if VariantCustomPages == "1"
  if CurPageID = SystemCheckPage.ID then
  begin
    UpdateSystemCheckPage;
    LayoutSystemCheckPage;
    WizardForm.NextButton.Caption := CustomMessage('TfsContinue');
  end;

  if CurPageID = StartupModePage.ID then
  begin
    UpdateStartupModePageState;
    LayoutStartupModePage;
    WizardForm.NextButton.Caption := CustomMessage('TfsContinue');
  end;
#endif

  if CurPageID = wpReady then
  begin
    if InitialInstallWasUpdate then
    begin
      WizardForm.PageNameLabel.Caption := CustomMessage('TfsUpdateReadyCaption');
      WizardForm.PageDescriptionLabel.Caption := CustomMessage('TfsUpdateReadyDescription');
      WizardForm.ReadyLabel.Caption := CustomMessage('TfsUpdateReadyPrompt');
      WizardForm.NextButton.Caption := CustomMessage('TfsUpdateNow');
    end
    else
    begin
      WizardForm.PageNameLabel.Caption := CustomMessage('TfsReadyCaption');
      WizardForm.PageDescriptionLabel.Caption := CustomMessage('TfsReadyDescription');
      WizardForm.ReadyLabel.Caption := CustomMessage('TfsReadyPrompt');
      WizardForm.NextButton.Caption := CustomMessage('TfsInstallNow');
    end;
  end;

  if CurPageID = wpInstalling then
  begin
    if InitialInstallWasUpdate then
    begin
      WizardForm.PageNameLabel.Caption := CustomMessage('TfsUpdatingCaption');
    end
    else
    begin
      WizardForm.PageNameLabel.Caption := CustomMessage('TfsInstallingCaption');
    end;
  end;

  if CurPageID = wpFinished then
  begin
    WizardForm.FinishedHeadingLabel.Caption := CustomMessage('TfsFinishedTitle');
    if InitialInstallWasUpdate then
    begin
      WizardForm.FinishedLabel.Caption := CustomMessage('TfsUpdateFinishedBody');
    end
    else
    begin
      WizardForm.FinishedLabel.Caption := CustomMessage('TfsFinishedBody');
    end;
  end;
end;

function IsWebView2RuntimeInstalled: Boolean;
var
  RuntimeVersion: string;
begin
  { Evergreen runtime, machine-wide (WOW6432Node) or per-user. }
  Result :=
    (RegQueryStringValue(HKLM32, WebView2ClientKey, 'pv', RuntimeVersion) and
     (RuntimeVersion <> '') and (RuntimeVersion <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, WebView2ClientKey, 'pv', RuntimeVersion) and
     (RuntimeVersion <> '') and (RuntimeVersion <> '0.0.0.0'));
end;

procedure InstallWebView2RuntimeIfMissing;
var
  SetupPath: string;
  ScriptPath: string;
  Script: string;
  ResultCode: Integer;
begin
  { Store sign-ins (OmniLibrary) render in WebView2. Windows 11 ships the
    runtime; older systems get Microsoft's bootstrapper. Best effort: TFS
    itself runs without it, so a failure must never stop the install. }
  if IsWebView2RuntimeInstalled then
  begin
    Log('The Microsoft Edge WebView2 Runtime is already installed.');
    Exit;
  end;

  UpdateSetupStatus('Installing the Microsoft Edge WebView2 Runtime...');
  try
    DownloadTemporaryFile(WebView2BootstrapperUrl, 'MicrosoftEdgeWebview2Setup.exe', '', nil);
  except
    Log('The WebView2 bootstrapper could not be downloaded: ' + GetExceptionMessage);
    Exit;
  end;

  SetupPath := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe');
  ScriptPath := ExpandConstant('{tmp}\tools-for-steam-verify-webview2.ps1');
  Script :=
    '$signature = Get-AuthenticodeSignature -LiteralPath ''' + EscapePowerShellSingleQuoted(SetupPath) + '''' + #13#10 +
    'if ($signature.Status -eq "Valid" -and $signature.SignerCertificate.Subject -match "O=Microsoft Corporation") { exit 0 }' + #13#10 +
    'exit 1' + #13#10;
  ResultCode := -1;
  if not (SaveStringToFile(ScriptPath, Script, False) and
     Exec(
       ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       '-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + ScriptPath + '"',
       '',
       SW_HIDE,
       ewWaitUntilTerminated,
       ResultCode) and
     (ResultCode = 0)) then
  begin
    Log('The downloaded WebView2 bootstrapper has no valid Microsoft signature; it was not started.');
    DeleteFile(SetupPath);
    Exit;
  end;

  if Exec(SetupPath, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
     (ResultCode = 0) then
  begin
    Log('The Microsoft Edge WebView2 Runtime was installed.');
  end
  else
  begin
    Log('The WebView2 bootstrapper returned code ' + IntToStr(ResultCode) + '; store sign-ins may need it later.');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    RemoveLegacyXboxModeIntegration;
    InstallWebView2RuntimeIfMissing;
    InstallPawnIOIfNeeded;
    InstallMandatoryHandheldReplacement;
    InstallRtssIfNeeded;
    ConfigureRtssProfileAccess;
    RemoveLegacyFpsHelperTask;
    RegisterElevatedHelperTaskSupport;
    ResumeElevatedHelperTasks;
    SanitizeSteamAutostartSources;
    { Startup mode finalization is deliberately deferred until setup teardown,
      so nothing relaunches TFS or Steam while driver and helper work is active. }
    InstallFinalizationReady := True;
  end;
end;

procedure LogInstallerDiagnostics;
begin
  Log(
    'Installer state: UpdateRequested=' + DiagnosticBool(UpdateInstallRequested) +
    '; InstallFinalizationReady=' + DiagnosticBool(InstallFinalizationReady) +
    '; StartupModeFinalized=' + DiagnosticBool(StartupModeFinalized) +
    '; SelectedMode=' + SelectedStartupMode +
    '; FinalizedMode=' + FinalizedStartupMode +
    '; Failures=' + DiagnosticFailureReason);
end;

procedure DeinitializeSetupCore;
var
  ResultCode: Integer;
begin
  if not InstallFinalizationReady then
  begin
    ResumeAfterFailedInstall;
    Exit;
  end;

  if not StartupModeFinalized then
  begin
    if not FinalizeInstalledStartupMode(True) then
    begin
      RecordDiagnosticFailure('Windows did not confirm a safe startup mode during installer finalization.');
      ResumeAfterFailedInstall;
      if not WizardSilent then
      begin
        MsgBox(
          'Windows did not confirm a safe Tools for Steam startup mode. Details are in the setup log.',
          mbError,
          MB_OK);
      end;
      Exit;
    end;
  end;

  StartGamepadHelperAfterInstall;

  if WizardSilent and
     FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
  begin
    Exec(
      ExpandConstant('{app}\{#MyAppExeName}'),
      '--tray',
      ExpandConstant('{app}'),
      SW_HIDE,
      ewNoWait,
      ResultCode);
  end;

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

procedure DeinitializeSetup;
begin
  try
    DeinitializeSetupCore;
  finally
    LogInstallerDiagnostics;
  end;
end;

function NeedRestart: Boolean;
begin
  Result := HandheldDriversInstalledThisRun;
end;

function IsUpdateOrUpgradeInstall: Boolean;
begin
  Result := UpdateInstallRequested or (GetExistingInstallLocation <> '');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if not InitialInstallWasUpdate then
  begin
    Exit;
  end;

  { Updates keep the existing directory, tasks, and startup mode. The ready
    page remains visible for interactive installs so nothing starts without
    an explicit confirmation. Silent update behavior is unchanged. }
  if (PageID = wpLicense) or
     (PageID = wpSelectDir) or
     (PageID = wpSelectTasks) then
  begin
    Result := True;
    Exit;
  end;

#if VariantCustomPages == "1"
  Result :=
    (PageID = SystemCheckPage.ID) or
    (PageID = StartupModePage.ID);
#endif
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CreateInstallerBackupSnapshot;
  LeaveLegacyXboxModeBeforeInstall;

  UpdateSetupStatus('Stopping every Tools for Steam, helper, and Steam process before install...');
  RequestToolsForSteamShutdown;
  Sleep(1000);
  if not StopElevatedHelperTasksForInstall then
  begin
    RecordDiagnosticFailure('The elevated Tools for Steam helper tasks could not be stopped before file replacement.');
    ResumeAfterFailedInstall;
    Result := 'The elevated Tools for Steam helpers could not be stopped. Installation was stopped before replacing files.';
    Exit;
  end;
  if not SuspendHandheldReplacementForInstall then
  begin
    RecordDiagnosticFailure('The physical handheld controller could not be suspended safely without restoring MSI Center M.');
    ResumeAfterFailedInstall;
    Result := 'Tools for Steam could not suspend the handheld controller replacement safely. Installation was stopped.';
    Exit;
  end;
  RestoreWindowsShellChrome;
  CloseProcess('ToolsForSteam.exe');
  CloseProcess('SteamLoader.exe');
  { Host process of the retired Xbox Mode package on older installs. }
  CloseProcess('ToolsForSteam.XboxHost.exe');
  if not EnsureToolsForSteamRuntimeStopped then
  begin
    RecordDiagnosticFailure('A Tools for Steam or elevated helper process remained active after forced shutdown.');
    ResumeAfterFailedInstall;
    Result := 'A Tools for Steam or helper process is still running. Installation was stopped before replacing files.';
    Exit;
  end;
  if not WaitForToolsForSteamExecutableRelease then
  begin
    RecordDiagnosticFailure('ToolsForSteam.exe remained locked after the coordinated shutdown.');
    ResumeAfterFailedInstall;
    Result := 'ToolsForSteam.exe is still in use after the coordinated shutdown. Installation was stopped before replacing files.';
    Exit;
  end;
  if not IsUpdateOrUpgradeInstall then
  begin
    if not RequestSteamShutdown then
    begin
      RecordDiagnosticFailure('Steam could not be stopped completely before the first install.');
      ResumeAfterFailedInstall;
      Result := 'Steam could not be stopped safely before installation. Close Steam and retry.';
      Exit;
    end;

    { Nothing may recreate Steam between shutdown and file replacement. }
    CloseProcess('ToolsForSteam.exe');
    if not EnsureToolsForSteamRuntimeStopped then
    begin
      RecordDiagnosticFailure('Tools for Steam restarted after Steam shutdown and before file replacement.');
      ResumeAfterFailedInstall;
      Result := 'Tools for Steam restarted unexpectedly before installation. Retry the install.';
      Exit;
    end;
  end;
  RestoreWindowsShellChrome;
  Result := '';
end;
