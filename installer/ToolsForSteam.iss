#define MyAppName "Tools for Steam"
#define MyAppShortName "TFS"
#define MyAppVersion "0.4.0"
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
#ifndef XboxHostBuildVersion
  #error XboxHostBuildVersion must match the signed MSIX manifest and be supplied by the build script.
#endif
#ifndef XboxHostRequiresDeveloperMode
  #error XboxHostRequiresDeveloperMode must match the SCCD policy selected by the build script.
#endif
#ifndef XboxHostPayloadDir
  #error XboxHostPayloadDir must point to the immutable Xbox host payload snapshot selected by the build script.
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
PrivilegesRequired=admin
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
Source: "{#PayloadDir}\{#MyAppExeName}"; DestName: "ToolsForSteamUpdateHelper.exe"; Flags: dontcopy noencryption
Source: "{#XboxHostPayloadDir}\ToolsForSteam.XboxHost.msix"; DestDir: "{app}\XboxMode"; Flags: ignoreversion
Source: "{#XboxHostPayloadDir}\ToolsForSteam.XboxHost.cer"; DestDir: "{app}\XboxMode"; Flags: ignoreversion
Source: "XboxModePackage.ps1"; DestDir: "{app}\XboxMode"; Flags: ignoreversion
Source: "XboxModeSession.ps1"; Flags: dontcopy
Source: "XboxModeDiagnostics.ps1"; Flags: dontcopy
Source: "Assets\ControllerGuide.png"; Flags: dontcopy
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\SteamLoader.App\ThirdParty\PawnIO\COPYING"; DestDir: "{app}\ThirdParty\PawnIO"; DestName: "COPYING-LGPL-2.1.txt"; Flags: ignoreversion
Source: "..\src\SteamLoader.App\ThirdParty\PawnIO\NOTICE.txt"; DestDir: "{app}\ThirdParty\PawnIO"; Flags: ignoreversion
Source: "..\src\SteamLoader.App\ThirdParty\PawnIO\PawnIO_setup.exe"; DestDir: "{app}\ThirdParty\PawnIO"; Flags: ignoreversion
Source: "..\src\SteamLoader.App\ThirdParty\DiscordSocialSdk\License-Notices.txt"; DestDir: "{app}\ThirdParty\DiscordSocialSdk"; Flags: ignoreversion
Source: "..\src\SteamLoader.App\ThirdParty\DiscordSocialSdk\NOTICE.txt"; DestDir: "{app}\ThirdParty\DiscordSocialSdk"; Flags: ignoreversion
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

[Icons]
Name: "{group}\Tools for Steam"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--tray"; WorkingDir: "{app}"
Name: "{group}\Start Tools for Steam"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--tray"; WorkingDir: "{app}"
Name: "{group}\Uninstall Tools for Steam"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Tools for Steam"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--tray"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\GCM\SteamTools"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletevalue uninsdeletekeyifempty

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM ToolsForSteam.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "CloseToolsForSteam"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM SteamLoader.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "CloseLegacySteamLoader"
Filename: "{app}\{#MyAppExeName}"; Parameters: "--restore-handheld-replacement"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RestoreHandheldReplacement"
Filename: "{app}\{#MyAppExeName}"; Parameters: "--remove-owned-handheld-drivers"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveOwnedHandheldDrivers"
Filename: "{app}\{#MyAppExeName}"; Parameters: "--restore-xbox-mode"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RestoreXboxModeSettings"
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ""Get-AppxPackage -Name 'GCM.ToolsForSteam.XboxHost' | Remove-AppxPackage"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveXboxModePackage"
Filename: "{sys}\certutil.exe"; Parameters: "-delstore TrustedPeople F12969BBD718F0BDAE024A02C716BEBF39141CA7"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveXboxModeCertificate"
Filename: "{sys}\certutil.exe"; Parameters: "-delstore TrustedPeople E38C2E0BDF195C5F4329102504002F4E0766FF99"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveLegacyXboxModeCertificate"
Filename: "{sys}\certutil.exe"; Parameters: "-delstore TrustedPeople B9FB8BD316D7A0FB2AC6AC10F204D562F9F8E251"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveOlderXboxModeCertificate"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""\ToolsForSteam\GamepadHelper"" /F"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveGamepadHelperTask"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""\ToolsForSteam\FpsHelper"" /F"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveFpsHelperTask"

[Code]
const
  XboxHostPackageName = 'GCM.ToolsForSteam.XboxHost';
  XboxHostPackageFamilyName = 'GCM.ToolsForSteam.XboxHost_kpg9gzy2ksp2j';
  XboxHostPackageVersion = '{#XboxHostBuildVersion}';
  XboxHostRequiresDeveloperMode = {#XboxHostRequiresDeveloperMode};
  XboxHostPublisher = 'CN=GCM Gaming Console Mode';
  XboxHostCertificateThumbprint = 'F12969BBD718F0BDAE024A02C716BEBF39141CA7';
  LegacyXboxHostCertificateThumbprint = 'E38C2E0BDF195C5F4329102504002F4E0766FF99';
  OlderXboxHostCertificateThumbprint = 'B9FB8BD316D7A0FB2AC6AC10F204D562F9F8E251';
  PawnIOSetupSha256 = '1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032';
  UsbIpSetupSha256 = '44451fe06f4186125c2a5ecd25b099c5560a61a60b1e56f5a0758e77a60afa44';
  HidHideSetupSha256 = 'f4bbbcb82e6258641b887c74bc81c4c5f66e4aa811808dfc304347687b7605f6';
  RtssPackageId = 'Guru3D.RTSS';
  RtssRequiredVersion = '7.3.7';

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
  ExternalModeRadio: TNewRadioButton;
  ExternalModeDescriptionLabel: TNewStaticText;
  GuideControllerImage: TBitmapImage;
  LaunchSettingsAfterSetup: Boolean;
  PostInstallSettingsArguments: string;
  ExistingInstallPath: string;
  SteamInstallPath: string;
  CreatedBackupPath: string;
  XboxModeSupportReady: Boolean;
  XboxCertificateInstalledThisRun: Boolean;
  XboxPackageExistedBeforeInstall: Boolean;
  InstallFinalizationReady: Boolean;
  XboxNativeApiChecked: Boolean;
  XboxNativeApiSupported: Boolean;
  XboxModeSuspendedForInstall: Boolean;
  XboxModeSessionStatePath: string;
  UpdateInstallRequested: Boolean;
  UpdateStartupModeToRestore: string;
  StartupModeFinalized: Boolean;
  FinalizedStartupMode: string;
  XboxModeFailureReason: string;
  XboxDeveloperModeChangedThisRun: Boolean;
  XboxDeveloperModeValueExisted: Boolean;
  XboxDeveloperModePreviousValue: Cardinal;
  DiagnosticFailureReason: string;
  HandheldDriversInstalledThisRun: Boolean;
  ElevatedHelperTasksSuspended: Boolean;

function IsMsiClawA8: Boolean; forward;
function SelectedStartupMode: string; forward;

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

procedure RecordXboxModeFailure(Message: string);
begin
  XboxModeFailureReason := Message;
  RecordDiagnosticFailure(Message);
end;

function IsXboxModePlatformSupported: Boolean;
forward;

function DescribeStartupMode(Mode: string): string;
begin
  if Mode = 'shell' then
  begin
    Result := 'Shell';
  end
  else if Mode = 'tray' then
  begin
    Result := 'Tray';
  end
  else if (Mode = 'xbox') or (Mode = 'external') then
  begin
    Result := 'Xbox Mode';
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

function RunXboxPackageTool(Mode: string; CertificatePath: string; PackagePath: string): Boolean;
var
  ScriptPath: string;
  LogPath: string;
  Arguments: string;
  ResultCode: Integer;
begin
  ScriptPath := ExpandConstant('{app}\XboxMode\XboxModePackage.ps1');
  LogPath := ExpandConstant('{app}\data\xbox-mode-installer.log');
  if not FileExists(ScriptPath) then
  begin
    Log('Xbox Mode package verification tool is missing.');
    Result := False;
    Exit;
  end;

  Arguments :=
    '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + ScriptPath + '"' +
    ' -Mode "' + Mode + '"' +
    ' -CertificatePath "' + CertificatePath + '"' +
    ' -PackagePath "' + PackagePath + '"' +
    ' -ExpectedThumbprint "' + XboxHostCertificateThumbprint + '"' +
    ' -ExpectedPackageName "' + XboxHostPackageName + '"' +
    ' -ExpectedPackageFamilyName "' + XboxHostPackageFamilyName + '"' +
    ' -ExpectedPublisher "' + XboxHostPublisher + '"' +
    ' -ExpectedVersion "' + XboxHostPackageVersion + '"' +
    ' -LogPath "' + LogPath + '"';

  Result := Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Arguments,
    ExpandConstant('{app}\XboxMode'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) and (ResultCode = 0);
  if not Result then
  begin
    Log('Xbox Mode package tool failed in ' + Mode + ' mode with code ' + IntToStr(ResultCode) + '.');
  end;
end;

procedure RollBackXboxCertificateInstalledThisRun;
var
  ResultCode: Integer;
begin
  if not XboxCertificateInstalledThisRun then
  begin
    Exit;
  end;

  UpdateSetupStatus('Rolling back the Xbox Mode certificate...');
  if Exec(
      ExpandConstant('{sys}\certutil.exe'),
      '-delstore TrustedPeople "' + XboxHostCertificateThumbprint + '"',
      ExpandConstant('{app}\XboxMode'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and (ResultCode = 0) then
  begin
    XboxCertificateInstalledThisRun := False;
    Log('Xbox Mode certificate installed by this setup run was rolled back.');
  end
  else
  begin
    Log('Xbox Mode certificate rollback failed or was cancelled.');
  end;
end;

function IsXboxPackageInstalled: Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command "if (Get-AppxPackage -Name ''' + XboxHostPackageName + ''') { exit 0 } else { exit 1 }"',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) and (ResultCode = 0);
end;

procedure RollBackXboxPackageInstalledThisRun;
var
  ResultCode: Integer;
begin
  if XboxPackageExistedBeforeInstall then
  begin
    Exit;
  end;

  UpdateSetupStatus('Rolling back the Xbox Mode package...');
  Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command "Get-AppxPackage -Name ''' + XboxHostPackageName + ''' | Remove-AppxPackage -ErrorAction SilentlyContinue"',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
  Log('Xbox Mode package installed by this setup run was rolled back.');
end;

function MigrateXboxSigningCertificate(Thumbprint: string): Boolean;
var
  LegacyCertificateKeyPath: string;
  ResultCode: Integer;
begin
  Result := True;
  LegacyCertificateKeyPath :=
    'SOFTWARE\Microsoft\SystemCertificates\TrustedPeople\Certificates\' +
    Thumbprint;
  if not RegKeyExists(HKLM64, LegacyCertificateKeyPath) then
  begin
    Exit;
  end;

  UpdateSetupStatus('Migrating the Tools for Steam Xbox Mode signing certificate...');
  if IsXboxPackageInstalled then
  begin
    if not Exec(
        ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
        '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command "Get-AppxPackage -Name ''' + XboxHostPackageName + ''' | Remove-AppxPackage -ErrorAction Stop"',
        '',
        SW_HIDE,
        ewWaitUntilTerminated,
        ResultCode) or (ResultCode <> 0) then
    begin
      RecordXboxModeFailure('The Xbox Mode package signed with the retired certificate could not be removed. Result code: ' + IntToStr(ResultCode) + '.');
      Result := False;
      Exit;
    end;
    XboxPackageExistedBeforeInstall := False;
  end;

  if not Exec(
      ExpandConstant('{sys}\certutil.exe'),
      '-delstore TrustedPeople "' + Thumbprint + '"',
      ExpandConstant('{app}\XboxMode'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) or (ResultCode <> 0) then
  begin
    RecordXboxModeFailure('The retired Xbox Mode certificate could not be removed. Result code: ' + IntToStr(ResultCode) + '.');
    Result := False;
    Exit;
  end;

  Log('The retired Xbox Mode signing certificate was removed successfully.');
end;

function MigrateLegacyXboxSigningCertificates: Boolean;
begin
  Result := MigrateXboxSigningCertificate(LegacyXboxHostCertificateThumbprint);
  if Result then
  begin
    Result := MigrateXboxSigningCertificate(OlderXboxHostCertificateThumbprint);
  end;
end;

function IsXboxDeveloperModeEnabled: Boolean;
var
  DeveloperModeValue: Cardinal;
begin
  Result :=
    RegQueryDWordValue(
      HKLM64,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock',
      'AllowDevelopmentWithoutDevLicense',
      DeveloperModeValue) and
    (DeveloperModeValue = 1);
end;

function EnsureXboxDeveloperMode: Boolean;
begin
  Result := True;
  if XboxHostRequiresDeveloperMode = 0 then
  begin
    Exit;
  end;

  if IsXboxDeveloperModeEnabled then
  begin
    Log('Windows Developer Mode is already enabled for the Xbox Mode SCCD.');
    Exit;
  end;

  XboxDeveloperModeValueExisted := RegQueryDWordValue(
    HKLM64,
    'SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock',
    'AllowDevelopmentWithoutDevLicense',
    XboxDeveloperModePreviousValue);
  UpdateSetupStatus('Enabling Windows Developer Mode required by the Xbox Mode package...');
  if not RegWriteDWordValue(
      HKLM64,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock',
      'AllowDevelopmentWithoutDevLicense',
      1) then
  begin
    RecordXboxModeFailure('Windows Developer Mode could not be enabled for the Xbox Mode package.');
    Result := False;
    Exit;
  end;

  XboxDeveloperModeChangedThisRun := True;
  Result := IsXboxDeveloperModeEnabled;
  if Result then
  begin
    Log('Windows Developer Mode was enabled because the selected Xbox Mode package uses a development SCCD.');
  end
  else
  begin
    RecordXboxModeFailure('Windows did not confirm Developer Mode after the installer enabled it.');
  end;
end;

procedure RollBackXboxDeveloperModeEnabledThisRun;
begin
  if not XboxDeveloperModeChangedThisRun then
  begin
    Exit;
  end;

  if XboxDeveloperModeValueExisted then
  begin
    RegWriteDWordValue(
      HKLM64,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock',
      'AllowDevelopmentWithoutDevLicense',
      XboxDeveloperModePreviousValue);
  end
  else
  begin
    RegDeleteValue(
      HKLM64,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock',
      'AllowDevelopmentWithoutDevLicense');
  end;
  XboxDeveloperModeChangedThisRun := False;
  Log('Windows Developer Mode was restored after the Xbox Mode installation failed.');
end;

procedure InstallXboxModeSupport;
var
  CertificatePath: string;
  PackagePath: string;
  CertificateKeyPath: string;
  ResultCode: Integer;
begin
  XboxModeSupportReady := False;
  XboxModeFailureReason := '';
  XboxCertificateInstalledThisRun := False;
  XboxDeveloperModeChangedThisRun := False;
  XboxDeveloperModeValueExisted := False;
  XboxDeveloperModePreviousValue := 0;
  XboxPackageExistedBeforeInstall := IsXboxPackageInstalled;

  if (XboxHostRequiresDeveloperMode = 1) and
     (SelectedStartupMode <> 'xbox') then
  begin
    Log('Xbox Mode package installation skipped because the Developer Mode fallback was not selected.');
    Exit;
  end;

  if not IsXboxModePlatformSupported then
  begin
    RecordXboxModeFailure('The Windows build or Gaming FSE API is not supported.');
    Log('Xbox Mode package installation skipped because this Windows build is unsupported.');
    Exit;
  end;

  CertificatePath := ExpandConstant('{app}\XboxMode\ToolsForSteam.XboxHost.cer');
  PackagePath := ExpandConstant('{app}\XboxMode\ToolsForSteam.XboxHost.msix');
  CertificateKeyPath :=
    'SOFTWARE\Microsoft\SystemCertificates\TrustedPeople\Certificates\' +
    XboxHostCertificateThumbprint;

  if not FileExists(CertificatePath) or not FileExists(PackagePath) then
  begin
    RecordXboxModeFailure('The Xbox Mode certificate or MSIX payload is missing after file installation.');
    Log('Xbox Mode support files are missing from the installed payload.');
    Exit;
  end;

  if not EnsureXboxDeveloperMode then
  begin
    RollBackXboxDeveloperModeEnabledThisRun;
    Exit;
  end;

  UpdateSetupStatus('Verifying the Xbox Mode package and capability policy...');
  if not RunXboxPackageTool('VerifyPayload', CertificatePath, PackagePath) then
  begin
    RecordXboxModeFailure('The Xbox Mode payload or its capability policy failed verification.');
    Log('Xbox Mode payload verification failed before certificate installation.');
    RollBackXboxDeveloperModeEnabledThisRun;
    Exit;
  end;

  if not MigrateLegacyXboxSigningCertificates then
  begin
    Log('Xbox Mode signing certificate migration failed.');
    RollBackXboxDeveloperModeEnabledThisRun;
    Exit;
  end;

  if not RegKeyExists(HKLM64, CertificateKeyPath) then
  begin
    UpdateSetupStatus('Installing the Tools for Steam Xbox Mode certificate...');
    if not Exec(
        ExpandConstant('{sys}\certutil.exe'),
        '-f -addstore TrustedPeople "' + CertificatePath + '"',
        ExpandConstant('{app}\XboxMode'),
        SW_HIDE,
        ewWaitUntilTerminated,
        ResultCode) or (ResultCode <> 0) then
    begin
      RecordXboxModeFailure('The Xbox Mode certificate could not be installed into LocalMachine TrustedPeople. Result code: ' + IntToStr(ResultCode) + '.');
      Log('Xbox Mode certificate installation failed or was cancelled.');
      RollBackXboxDeveloperModeEnabledThisRun;
      Exit;
    end;
    XboxCertificateInstalledThisRun := True;
  end;

  UpdateSetupStatus('Registering Tools for Steam as the Windows Xbox Mode Home app...');
  if not RunXboxPackageTool('InstallAndVerify', CertificatePath, PackagePath) then
  begin
    RecordXboxModeFailure('Windows failed to register or verify the Xbox Mode MSIX package and its windows.gamingApp extension.');
    Log('Xbox Mode package registration or extension verification failed.');
    RollBackXboxPackageInstalledThisRun;
    RollBackXboxCertificateInstalledThisRun;
    RollBackXboxDeveloperModeEnabledThisRun;
    Exit;
  end;

  UpdateSetupStatus('Checking Windows Gaming FSE compatibility...');
  if not Exec(
      ExpandConstant('{app}\{#MyAppExeName}'),
      '--check-xbox-mode-support',
      ExpandConstant('{app}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) or (ResultCode <> 0) then
  begin
    RecordXboxModeFailure('The installed TFS runtime did not confirm Windows Gaming FSE compatibility. Result code: ' + IntToStr(ResultCode) + '.');
    Log('The installed TFS runtime did not confirm Xbox Mode compatibility.');
    RollBackXboxPackageInstalledThisRun;
    RollBackXboxCertificateInstalledThisRun;
    RollBackXboxDeveloperModeEnabledThisRun;
    Exit;
  end;

  XboxModeSupportReady := True;
  XboxModeFailureReason := '';
  Log('Tools for Steam Xbox Mode support is installed and ready.');
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

function IsXboxNativeApiAvailable: Boolean;
var
  ScriptPath: string;
  Script: string;
  ResultCode: Integer;
begin
  if XboxNativeApiChecked then
  begin
    Result := XboxNativeApiSupported;
    Exit;
  end;

  XboxNativeApiChecked := True;
  ScriptPath := ExpandConstant('{tmp}\tfs-check-xbox-fse-api.ps1');
  Script :=
    '$source = @''' + #13#10 +
    'using System;' + #13#10 +
    'using System.Runtime.InteropServices;' + #13#10 +
    'public static class TfsXboxApiCheck {' + #13#10 +
    '  [DllImport("kernel32.dll", EntryPoint = "LoadLibraryW", CharSet = CharSet.Unicode, SetLastError = true)]' + #13#10 +
    '  private static extern IntPtr LoadLibrary(string name);' + #13#10 +
    '  [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]' + #13#10 +
    '  private static extern IntPtr GetProcAddress(IntPtr module, string name);' + #13#10 +
    '  [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);' + #13#10 +
    '  public static bool Available() {' + #13#10 +
    '    var module = LoadLibrary("api-ms-win-gaming-experience-l1-1-0.dll");' + #13#10 +
    '    if (module == IntPtr.Zero) return false;' + #13#10 +
    '    try {' + #13#10 +
    '      return GetProcAddress(module, "IsGamingFullScreenExperienceActive") != IntPtr.Zero &&' + #13#10 +
    '             GetProcAddress(module, "SetGamingFullScreenExperience") != IntPtr.Zero;' + #13#10 +
    '    } finally { FreeLibrary(module); }' + #13#10 +
    '  }' + #13#10 +
    '}' + #13#10 +
    '''@' + #13#10 +
    'try { Add-Type -TypeDefinition $source -ErrorAction Stop; if ([TfsXboxApiCheck]::Available()) { exit 0 } } catch {}' + #13#10 +
    'exit 1' + #13#10;

  XboxNativeApiSupported :=
    SaveStringToFile(ScriptPath, Script, False) and
    Exec(
      ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + ScriptPath + '"',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and
    (ResultCode = 0);
  Result := XboxNativeApiSupported;
end;

function IsXboxModePlatformSupported: Boolean;
var
  BuildNumber: Integer;
  BuildRevision: Integer;
begin
  BuildNumber := GetWindowsBuildNumber;
  BuildRevision := GetWindowsBuildRevision;
  Result := (
    (BuildNumber > 26100) or
    ((BuildNumber = 26100) and (BuildRevision >= 7019))) and
    IsXboxNativeApiAvailable;
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
      'You can still repair the elevated Xbox Mode helper later after installation.' + #13#10#13#10 +
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
    Log('The elevated Xbox Mode helper is already installed; refreshing it and sanitizing conflicting startup tasks.');
  end;

  UpdateSetupStatus('Installing the elevated Xbox Mode helper...');

  if not Exec(
    ExpandConstant('{app}\{#MyAppExeName}'),
    '--register-installed-helper-tasks',
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    Log('Elevated helper task registration was skipped or cancelled.');
    ShowElevatedHelperInstallWarning('The elevated Xbox Mode helper was not prepared during installation.');
    Exit;
  end;

  if not IsGamepadHelperTaskInstalled then
  begin
    Log('Elevated Xbox Mode helper registration did not complete successfully.');
    ShowElevatedHelperInstallWarning('The elevated Xbox Mode helper could not be prepared during installation.');
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
  if not IsMsiClawA8 then
  begin
    Log('PawnIO installation skipped because this device is not an MSI Claw A8 (MS-1T8K).');
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
      if Pos('"xbox"', SettingsJsonLower) > 0 then
      begin
        Result := 'xbox';
      end
      else if Pos('"external"', SettingsJsonLower) > 0 then
      begin
        Result := 'xbox';
      end
      else if Pos('"tray"', SettingsJsonLower) > 0 then
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
    'Choose Shell, Xbox Mode, or eTray, then let the installer keep a rollback snapshot ' +
    'before it changes any Windows startup hooks.';

  if IsXboxModePlatformSupported then
  begin
    SystemCheckWindowsLabel.Caption :=
      'Windows build: ' + IntToStr(GetWindowsBuildNumber()) + '.' + IntToStr(GetWindowsBuildRevision()) +
      ' - ready for TFS and Xbox Mode.';
  end
  else
  begin
    SystemCheckWindowsLabel.Caption :=
      'Windows build: ' + IntToStr(GetWindowsBuildNumber()) + '.' + IntToStr(GetWindowsBuildRevision()) +
      ' - Xbox Mode requires Windows 26100.7019 or newer and will not be installed or shown.';
  end;

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
    SystemCheckInstallLabel.Caption := 'Existing TFS install: found at ' + ExistingInstallPath + ' (' + DescribeStartupMode(CurrentMode) + ' mode).';
  end
  else
  begin
    SystemCheckInstallLabel.Caption := 'Existing TFS install: not found. This will be a fresh install.';
  end;

  SystemCheckHelperLabel.Caption :=
    'Performance: existing RTSS binaries and settings are reused; Setup only configures per-user game-profile access, and downloads RTSS 7.3.7 when missing. The elevated helper is only used for Xbox Mode. ' +
    'This check is deferred so the setup window always opens cleanly on fresh Windows installs.';

  SystemCheckRollbackLabel.Caption := 'Rollback: a snapshot and rollback script will be written before startup settings are changed.';
end;

procedure UpdateStartupModePageState;
begin
  TrayModeRadio.Visible := True;
  TrayModeDescriptionLabel.Visible := True;
  ExternalModeRadio.Visible := IsXboxModePlatformSupported;
  ExternalModeDescriptionLabel.Visible := IsXboxModePlatformSupported;
  if not IsXboxModePlatformSupported and ExternalModeRadio.Checked then
  begin
    ShellModeRadio.Checked := True;
  end;
end;

procedure StartupModeSelectionChanged(Sender: TObject);
begin
  UpdateStartupModePageState;
end;

function SelectedStartupMode: string;
begin
#if VariantCustomPages == "0"
  Result := 'shell';
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
    if Result = '' then
    begin
      Result := 'shell';
    end;
    if (Result = 'xbox') and not IsXboxModePlatformSupported then
    begin
      Result := 'tray';
    end;
  end
  else if ExternalModeRadio.Checked then
  begin
    Result := 'xbox';
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
    'Pick the startup style you want now. You can still switch later inside TFS.');

  StartupModeSummaryLabel := TNewStaticText.Create(WizardForm);
  StartupModeSummaryLabel.Parent := StartupModePage.Surface;
  StartupModeSummaryLabel.Left := 0;
  StartupModeSummaryLabel.Top := ScaleY(4);
  StartupModeSummaryLabel.Width := StartupModePage.SurfaceWidth;
  StartupModeSummaryLabel.Height := ScaleY(42);
  StartupModeSummaryLabel.AutoSize := False;
  StartupModeSummaryLabel.WordWrap := True;
  if IsXboxModePlatformSupported then
  begin
    StartupModeSummaryLabel.Caption :=
      'Shell Mode (Recommended) gives the cleanest couch setup, so it is preselected. ' +
      'Xbox Mode and eTray stay available below it and all three modes cleanly replace each other.';
  end
  else
  begin
    StartupModeSummaryLabel.Caption :=
      'Shell Mode (Recommended) and eTray are available. Xbox Mode is hidden because this Windows build does not provide the required Gaming FSE support.';
  end;

  ShellModeRadio := TNewRadioButton.Create(WizardForm);
  ShellModeRadio.Parent := StartupModePage.Surface;
  ShellModeRadio.Left := 0;
  ShellModeRadio.Top := ScaleY(60);
  ShellModeRadio.Width := StartupModePage.SurfaceWidth;
  ShellModeRadio.Caption := 'Shell Mode (Recommended)';
  ShellModeRadio.Checked := (CurrentMode = '') or (CurrentMode = 'shell');
  ShellModeRadio.OnClick := @StartupModeSelectionChanged;

  ShellModeDescriptionLabel := TNewStaticText.Create(WizardForm);
  ShellModeDescriptionLabel.Parent := StartupModePage.Surface;
  ShellModeDescriptionLabel.Left := ScaleX(20);
  ShellModeDescriptionLabel.Top := ScaleY(84);
  ShellModeDescriptionLabel.Width := StartupModePage.SurfaceWidth - ScaleX(20);
  ShellModeDescriptionLabel.Height := ScaleY(34);
  ShellModeDescriptionLabel.AutoSize := False;
  ShellModeDescriptionLabel.WordWrap := True;
  ShellModeDescriptionLabel.Caption :=
    'Boot TFS before Explorer, sync launchers, open Steam Big Picture, then put the normal Windows shell back behind Steam.';

  TrayModeRadio := TNewRadioButton.Create(WizardForm);
  TrayModeRadio.Parent := StartupModePage.Surface;
  TrayModeRadio.Left := 0;
  TrayModeRadio.Top := ScaleY(128);
  TrayModeRadio.Width := StartupModePage.SurfaceWidth;
  TrayModeRadio.Caption := 'eTray mode';
  TrayModeRadio.Checked := CurrentMode = 'tray';
  TrayModeRadio.OnClick := @StartupModeSelectionChanged;

  TrayModeDescriptionLabel := TNewStaticText.Create(WizardForm);
  TrayModeDescriptionLabel.Parent := StartupModePage.Surface;
  TrayModeDescriptionLabel.Left := ScaleX(20);
  TrayModeDescriptionLabel.Top := ScaleY(152);
  TrayModeDescriptionLabel.Width := StartupModePage.SurfaceWidth - ScaleX(20);
  TrayModeDescriptionLabel.Height := ScaleY(36);
  TrayModeDescriptionLabel.AutoSize := False;
  TrayModeDescriptionLabel.WordWrap := True;
  TrayModeDescriptionLabel.Caption :=
    'Keep the normal Windows shell and start TFS quietly with Windows as a tray-style launcher service.';

  ExternalModeRadio := TNewRadioButton.Create(WizardForm);
  ExternalModeRadio.Parent := StartupModePage.Surface;
  ExternalModeRadio.Left := 0;
  ExternalModeRadio.Top := ScaleY(194);
  ExternalModeRadio.Width := StartupModePage.SurfaceWidth;
  ExternalModeRadio.Caption := 'Xbox Mode';
  ExternalModeRadio.Checked := (CurrentMode = 'xbox') or (CurrentMode = 'external');
  ExternalModeRadio.OnClick := @StartupModeSelectionChanged;

  ExternalModeDescriptionLabel := TNewStaticText.Create(WizardForm);
  ExternalModeDescriptionLabel.Parent := StartupModePage.Surface;
  ExternalModeDescriptionLabel.Left := ScaleX(20);
  ExternalModeDescriptionLabel.Top := ScaleY(218);
  ExternalModeDescriptionLabel.Width := StartupModePage.SurfaceWidth - ScaleX(20);
  ExternalModeDescriptionLabel.Height := ScaleY(60);
  ExternalModeDescriptionLabel.AutoSize := False;
  ExternalModeDescriptionLabel.WordWrap := True;
  if XboxHostRequiresDeveloperMode = 1 then
  begin
    ExternalModeDescriptionLabel.Caption :=
      'Use the TFS Gaming Home package. This enables Windows Developer Mode system-wide, as required for the custom Xbox Home capability. Explorer remains the shell.';
  end
  else
  begin
    ExternalModeDescriptionLabel.Caption :=
      'Use the Microsoft-authorized TFS Gaming Home package. Explorer remains the Windows shell and eTray startup is disabled.';
  end;

  GuidePage := CreateCustomPage(
    wpSelectDir,
    'Controller guide',
    'Steam and in-game controller shortcuts at a glance.');

  ExtractTemporaryFile('ControllerGuide.png');
  GuideControllerImage := TBitmapImage.Create(WizardForm);
  GuideControllerImage.Parent := GuidePage.Surface;
  GuideControllerImage.Left := 0;
  GuideControllerImage.Width := GuidePage.SurfaceWidth;
  GuideControllerImage.Height := (GuideControllerImage.Width * 9) div 16;
  GuideControllerImage.Top := (GuidePage.SurfaceHeight - GuideControllerImage.Height) div 2;
  GuideControllerImage.Stretch := True;
  GuideControllerImage.Center := True;
  GuideControllerImage.BackColor := clNone;
  GuideControllerImage.PngImage.LoadFromFile(ExpandConstant('{tmp}\ControllerGuide.png'));

  if not (ShellModeRadio.Checked or TrayModeRadio.Checked or ExternalModeRadio.Checked) then
  begin
    ShellModeRadio.Checked := True;
  end;

  UpdateSystemCheckPage;
  UpdateStartupModePageState;
#endif
end;

function ShouldSuspendXboxModeForInstall: Boolean;
var
  GamingHomeApp: string;
begin
  if GetExistingInstallLocation = '' then
  begin
    Result := False;
    Exit;
  end;

  GamingHomeApp := GetRegistryStringValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\GamingConfiguration',
    'GamingHomeApp');
  Result :=
    (CompareText(GamingHomeApp, XboxHostPackageFamilyName + '!App') = 0) or
    (CompareText(UpdateStartupModeToRestore, 'xbox') = 0);
end;

function RunXboxModeSessionTool(Mode: string): Boolean;
var
  ScriptPath: string;
  LogPath: string;
  ResultCode: Integer;
begin
  ExtractTemporaryFile('XboxModeSession.ps1');
  ScriptPath := ExpandConstant('{tmp}\XboxModeSession.ps1');
  LogPath := AddBackslash(ExpandConstant('{app}')) + 'data\xbox-mode-installer.log';
  Result := Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + ScriptPath + '"' +
    ' -Mode "' + Mode + '"' +
    ' -StatePath "' + XboxModeSessionStatePath + '"' +
    ' -LogPath "' + LogPath + '"',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) and (ResultCode = 0);
  if not Result then
  begin
    Log('Xbox Mode session tool failed in ' + Mode + ' mode with code ' + IntToStr(ResultCode) + '.');
  end;
end;

function SuspendXboxModeForInstall: Boolean;
begin
  Result := True;
  if not ShouldSuspendXboxModeForInstall then
  begin
    Exit;
  end;

  XboxModeSessionStatePath := ExpandConstant('{tmp}\tfs-xbox-mode-install-state.json');
  DeleteFile(XboxModeSessionStatePath);
  UpdateSetupStatus('Leaving Xbox Mode safely before updating files and the Gaming Home package...');
  Result := RunXboxModeSessionTool('Suspend');
  XboxModeSuspendedForInstall := Result;
end;

procedure RestoreSuspendedXboxModeAfterFailure;
begin
  ResumeElevatedHelperTasks;
  if not XboxModeSuspendedForInstall then
  begin
    Exit;
  end;

  UpdateSetupStatus('Restoring the Xbox Mode state from before the failed install...');
  if RunXboxModeSessionTool('Restore') then
  begin
    XboxModeSuspendedForInstall := False;
    DeleteFile(XboxModeSessionStatePath);
  end;
end;

function ApplyInstalledStartupMode(Mode: string): Boolean;
var
  ResultCode: Integer;
begin
  Result :=
    FileExists(ExpandConstant('{app}\{#MyAppExeName}')) and
    Exec(
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
  if (TargetMode = 'xbox') and not XboxModeSupportReady then
  begin
    if UpdateInstallRequested and IsXboxModePlatformSupported then
    begin
      Result := False;
      Exit;
    end;
    TargetMode := 'tray';
  end;

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

function IsShellMode: Boolean;
begin
  Result := SelectedStartupMode = 'shell';
end;

function IsTrayMode: Boolean;
begin
  Result := SelectedStartupMode = 'tray';
end;

function IsExternalMode: Boolean;
begin
  Result := (SelectedStartupMode = 'xbox') and XboxModeSupportReady;
end;

function IsXboxModeFallback: Boolean;
begin
  Result := (SelectedStartupMode = 'xbox') and not XboxModeSupportReady;
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

#endif

end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    InstallXboxModeSupport;
    InstallPawnIOIfNeeded;
    InstallMandatoryHandheldReplacement;
    InstallRtssIfNeeded;
    ConfigureRtssProfileAccess;
    RemoveLegacyFpsHelperTask;
    RegisterElevatedHelperTaskSupport;
    ResumeElevatedHelperTasks;
    SanitizeSteamAutostartSources;
    { Startup mode finalization is deliberately deferred until setup teardown.
      Xbox Mode must not relaunch TFS or Steam while package and helper work is active. }
    InstallFinalizationReady := True;
  end;
end;

procedure RunXboxModeDiagnostics;
var
  ScriptPath: string;
  ReportPath: string;
  InstallerState: string;
  ResultCode: Integer;
begin
  ReportPath := AddBackslash(ExpandConstant('{src}')) + 'ToolsForSteam-XboxMode-Diagnostics.log';
  InstallerState :=
    'UpdateRequested=' + DiagnosticBool(UpdateInstallRequested) +
    '; InstallFinalizationReady=' + DiagnosticBool(InstallFinalizationReady) +
    '; StartupModeFinalized=' + DiagnosticBool(StartupModeFinalized) +
    '; XboxModeSupportReady=' + DiagnosticBool(XboxModeSupportReady) +
    '; XboxModeSuspended=' + DiagnosticBool(XboxModeSuspendedForInstall);

  try
    ExtractTemporaryFile('XboxModeDiagnostics.ps1');
    ScriptPath := ExpandConstant('{tmp}\XboxModeDiagnostics.ps1');
    ResultCode := -1;
    if Exec(
        ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
        '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + ScriptPath + '"' +
        ' -RequestedOutputPath "' + ReportPath + '"' +
        ' -InstallerLogPath "' + ExpandConstant('{log}') + '"' +
        ' -InstallRoot "' + ExpandConstant('{app}') + '"' +
        ' -SetupPath "' + ExpandConstant('{srcexe}') + '"' +
        ' -SelectedMode "' + SelectedStartupMode + '"' +
        ' -FinalizedMode "' + FinalizedStartupMode + '"' +
        ' -FailureReason "' + DiagnosticFailureReason + '"' +
        ' -InstallerState "' + InstallerState + '"' +
        ' -ExpectedPackageName "' + XboxHostPackageName + '"' +
        ' -ExpectedPackageFamilyName "' + XboxHostPackageFamilyName + '"' +
        ' -ExpectedThumbprint "' + XboxHostCertificateThumbprint + '"',
        '',
        SW_HIDE,
        ewWaitUntilTerminated,
        ResultCode) and (ResultCode = 0) then
    begin
      Log('Xbox Mode diagnostics written next to the setup executable: ' + ReportPath);
    end
    else
    begin
      Log('Xbox Mode diagnostics collector returned code ' + IntToStr(ResultCode) + '. Check the Desktop or the installation data directory for its fallback report.');
    end;
  except
    Log('Xbox Mode diagnostics collector failed to start: ' + GetExceptionMessage);
  end;
end;

procedure DeinitializeSetupCore;
var
  ResultCode: Integer;
begin
  if not InstallFinalizationReady then
  begin
    RestoreSuspendedXboxModeAfterFailure;
    Exit;
  end;

  if not StartupModeFinalized then
  begin
    if not FinalizeInstalledStartupMode(True) then
    begin
      RecordDiagnosticFailure('Windows did not confirm a safe startup mode during installer finalization.');
      RestoreSuspendedXboxModeAfterFailure;
      if not WizardSilent then
      begin
        MsgBox(
          'Windows did not confirm a safe Tools for Steam startup mode. The state from before installation was restored. Details are in data\xbox-mode-installer.log.',
          mbError,
          MB_OK);
      end;
      Exit;
    end;
  end;

  XboxModeSuspendedForInstall := False;
  DeleteFile(XboxModeSessionStatePath);
  StartGamepadHelperAfterInstall;

  if WizardSilent and (FinalizedStartupMode <> 'xbox') and
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
    RunXboxModeDiagnostics;
    if (XboxModeFailureReason <> '') and not WizardSilent then
    begin
      MsgBox(
        'Tools for Steam could not enable Xbox Mode:' + #13#10 +
        XboxModeFailureReason + #13#10#13#10 +
        'A complete diagnostic report was created as ToolsForSteam-XboxMode-Diagnostics.log next to this setup EXE. If that folder is read-only, the report is on your Desktop.',
        mbError,
        MB_OK);
    end;
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

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CreateInstallerBackupSnapshot;
  if not SuspendXboxModeForInstall then
  begin
    RecordDiagnosticFailure('The installer could not leave the active Xbox Mode session safely before replacing files.');
    Result := 'Tools for Steam could not leave Xbox Mode safely. Installation was stopped before any files were replaced. See data\xbox-mode-installer.log.';
    Exit;
  end;

  UpdateSetupStatus('Stopping every Tools for Steam, helper, Xbox Host, and Steam process before install...');
  RequestToolsForSteamShutdown;
  Sleep(1000);
  if not StopElevatedHelperTasksForInstall then
  begin
    RecordDiagnosticFailure('The elevated Tools for Steam helper tasks could not be stopped before file replacement.');
    RestoreSuspendedXboxModeAfterFailure;
    Result := 'The elevated Tools for Steam helpers could not be stopped. Installation was stopped before replacing files.';
    Exit;
  end;
  if not SuspendHandheldReplacementForInstall then
  begin
    RecordDiagnosticFailure('The physical handheld controller could not be suspended safely without restoring MSI Center M.');
    RestoreSuspendedXboxModeAfterFailure;
    Result := 'Tools for Steam could not suspend the handheld controller replacement safely. Installation was stopped.';
    Exit;
  end;
  RestoreWindowsShellChrome;
  CloseProcess('ToolsForSteam.exe');
  CloseProcess('SteamLoader.exe');
  CloseProcess('ToolsForSteam.XboxHost.exe');
  if not EnsureToolsForSteamRuntimeStopped then
  begin
    RecordDiagnosticFailure('A Tools for Steam, elevated helper, or Xbox Host process remained active after forced shutdown.');
    RestoreSuspendedXboxModeAfterFailure;
    Result := 'A Tools for Steam or helper process is still running. Installation was stopped before replacing files.';
    Exit;
  end;
  if not WaitForToolsForSteamExecutableRelease then
  begin
    RecordDiagnosticFailure('ToolsForSteam.exe remained locked after the coordinated shutdown.');
    RestoreSuspendedXboxModeAfterFailure;
    Result := 'ToolsForSteam.exe is still in use after the coordinated shutdown. Installation was stopped before replacing files.';
    Exit;
  end;
  if (not IsUpdateOrUpgradeInstall) or (SelectedStartupMode = 'xbox') then
  begin
    if IsUpdateOrUpgradeInstall then
    begin
      UpdateSetupStatus('Preparing a clean Steam hand-off for the updated Xbox Mode...');
    end;

    if not RequestSteamShutdown then
    begin
      RecordDiagnosticFailure('Steam could not be stopped completely before the Xbox Mode hand-off.');
      RestoreSuspendedXboxModeAfterFailure;
      Result := 'Steam could not be stopped safely before entering the updated Xbox Mode. Close Steam and retry the update.';
      Exit;
    end;

    { Nothing may recreate Steam between shutdown and file replacement. }
    CloseProcess('ToolsForSteam.exe');
    CloseProcess('ToolsForSteam.XboxHost.exe');
    if not EnsureToolsForSteamRuntimeStopped then
    begin
      RecordDiagnosticFailure('Tools for Steam restarted after Steam shutdown and before Xbox Mode update.');
      RestoreSuspendedXboxModeAfterFailure;
      Result := 'Tools for Steam restarted unexpectedly during the clean Xbox Mode hand-off. Retry the update.';
      Exit;
    end;
  end;
  RestoreWindowsShellChrome;
  Result := '';
end;
