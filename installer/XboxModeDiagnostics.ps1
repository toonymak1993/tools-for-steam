[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RequestedOutputPath,

    [string]$InstallerLogPath = "",
    [string]$InstallRoot = "",
    [string]$SetupPath = "",
    [string]$SelectedMode = "",
    [string]$FinalizedMode = "",
    [string]$FailureReason = "",
    [string]$InstallerState = "",
    [string]$ExpectedPackageName = "GCM.ToolsForSteam.XboxHost",
    [string]$ExpectedPackageFamilyName = "GCM.ToolsForSteam.XboxHost_kpg9gzy2ksp2j",
    [string]$ExpectedThumbprint = "E38C2E0BDF195C5F4329102504002F4E0766FF99"
)

$ErrorActionPreference = "Continue"
$report = [System.Text.StringBuilder]::new()

function Add-Line {
    param([AllowEmptyString()][string]$Text = "")
    [void]$report.AppendLine($Text)
}

function Add-Section {
    param([string]$Title)
    Add-Line
    Add-Line ("=" * 88)
    Add-Line $Title
    Add-Line ("=" * 88)
}

function Add-Value {
    param([string]$Name, $Value)
    Add-Line ("{0}: {1}" -f $Name, $(if ($null -eq $Value) { "<null>" } else { $Value }))
}

function Add-SafeBlock {
    param([string]$Name, [scriptblock]$Operation)

    Add-Line ("--- {0} ---" -f $Name)
    try {
        $value = & $Operation 2>&1 | Out-String -Width 4096
        Add-Line $value.TrimEnd()
    }
    catch {
        Add-Line ("FAILED: {0}" -f $_.Exception.ToString())
        Add-Line ($_ | Format-List * -Force | Out-String -Width 4096).TrimEnd()
    }
}

function Read-TextFile {
    param([string]$Path, [int]$MaximumLines = 4000)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return "<path not provided>"
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return "<missing: $Path>"
    }

    $lines = @(Get-Content -LiteralPath $Path -ErrorAction Stop)
    if ($lines.Count -le $MaximumLines) {
        return $lines -join [Environment]::NewLine
    }

    return @(
        "<showing last $MaximumLines of $($lines.Count) lines>"
        $lines | Select-Object -Last $MaximumLines
    ) -join [Environment]::NewLine
}

function Resolve-ReportPath {
    $candidates = @(
        $RequestedOutputPath,
        $(if ($InstallRoot) { Join-Path $InstallRoot "data\ToolsForSteam-XboxMode-Diagnostics.log" }),
        $(Join-Path ([Environment]::GetFolderPath("Desktop")) "ToolsForSteam-XboxMode-Diagnostics.log")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

    foreach ($candidate in $candidates) {
        try {
            $directory = Split-Path -Parent $candidate
            if ($directory) {
                New-Item -ItemType Directory -Force -Path $directory -ErrorAction Stop | Out-Null
            }
            [IO.File]::WriteAllText($candidate, "TFS Xbox Mode diagnostics are being collected...", [Text.UTF8Encoding]::new($true))
            return $candidate
        }
        catch {
        }
    }

    return Join-Path $env:TEMP "ToolsForSteam-XboxMode-Diagnostics.log"
}

$outputPath = Resolve-ReportPath

Add-Line "TOOLS FOR STEAM - XBOX MODE INSTALLATION DIAGNOSTICS"
Add-Line "Generated automatically. Send this complete file when reporting Xbox Mode installation problems."
Add-Value "Generated" (Get-Date -Format o)
Add-Value "ReportPath" $outputPath
Add-Value "RequestedReportPath" $RequestedOutputPath
Add-Value "SetupPath" $SetupPath
Add-Value "InstallRoot" $InstallRoot
Add-Value "InstallerLogPath" $InstallerLogPath
Add-Value "SelectedMode" $SelectedMode
Add-Value "FinalizedMode" $FinalizedMode
Add-Value "InstallerState" $InstallerState
Add-Value "FailureReason" $(if ($FailureReason) { $FailureReason } else { "<none recorded>" })
Add-Value "CommandLine" ([Environment]::CommandLine)
Add-Value "User" ([Security.Principal.WindowsIdentity]::GetCurrent().Name)
Add-Value "Elevated" (([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
Add-Value "ProcessArchitecture" ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture)
Add-Value "OSArchitecture" ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture)
Add-Value "PowerShell" $PSVersionTable.PSVersion

Add-Section "SETUP EXECUTABLE AND PAYLOAD"
Add-SafeBlock "Setup file" {
    if (-not $SetupPath -or -not (Test-Path -LiteralPath $SetupPath -PathType Leaf)) {
        throw "Setup executable is not accessible at '$SetupPath'."
    }
    $file = Get-Item -LiteralPath $SetupPath
    $signature = Get-AuthenticodeSignature -LiteralPath $SetupPath
    [pscustomobject]@{
        FullName = $file.FullName
        Length = $file.Length
        Version = $file.VersionInfo.FileVersion
        SHA256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        SignatureStatus = $signature.Status
        SignatureStatusMessage = $signature.StatusMessage
        Signer = $signature.SignerCertificate.Subject
    } | Format-List
}
Add-SafeBlock "Installed Xbox payload" {
    $certificatePath = Join-Path $InstallRoot "XboxMode\ToolsForSteam.XboxHost.cer"
    $packagePath = Join-Path $InstallRoot "XboxMode\ToolsForSteam.XboxHost.msix"
    $certificate = if (Test-Path -LiteralPath $certificatePath) {
        [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
    }
    $signature = if (Test-Path -LiteralPath $packagePath) {
        Get-AuthenticodeSignature -LiteralPath $packagePath
    }
    [pscustomobject]@{
        CertificatePath = $certificatePath
        CertificateExists = (Test-Path -LiteralPath $certificatePath)
        CertificateThumbprint = $certificate.Thumbprint
        CertificateSubject = $certificate.Subject
        CertificateNotBefore = $certificate.NotBefore
        CertificateNotAfter = $certificate.NotAfter
        PackagePath = $packagePath
        PackageExists = (Test-Path -LiteralPath $packagePath)
        PackageLength = $(if (Test-Path -LiteralPath $packagePath) { (Get-Item -LiteralPath $packagePath).Length })
        PackageSHA256 = $(if (Test-Path -LiteralPath $packagePath) { (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash })
        PackageSignatureStatus = $signature.Status
        PackageSignerThumbprint = $signature.SignerCertificate.Thumbprint
    } | Format-List
    if ($certificate) { $certificate.Dispose() }
}
Add-SafeBlock "Packaged Xbox Gaming Home SCCD" {
    $packagePath = Join-Path $InstallRoot "XboxMode\ToolsForSteam.XboxHost.msix"
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "The Xbox Mode MSIX is missing."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $entry = $archive.Entries | Where-Object {
            $_.FullName -notmatch '[/\\]' -and $_.FullName -match '(?i)\.sccd$'
        } | Select-Object -First 1
        if (-not $entry) {
            throw "The package does not contain a root-level SCCD."
        }
        $reader = [IO.StreamReader]::new($entry.Open())
        try {
            [xml]$sccd = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    $capability = $sccd.SelectSingleNode("//*[local-name()='CustomCapability']")
    $authorizedEntities = $sccd.SelectSingleNode("//*[local-name()='AuthorizedEntities']")
    $catalogNode = $sccd.SelectSingleNode("//*[local-name()='Catalog']")
    $catalog = if ($catalogNode) { $catalogNode.InnerText.Trim() } else { "" }
    $catalogBytes = try { [Convert]::FromBase64String($catalog) } catch { @() }
    $developerMode = Get-ItemProperty `
        -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" `
        -Name "AllowDevelopmentWithoutDevLicense" `
        -ErrorAction SilentlyContinue
    $signatureValid = $false
    if ($catalogBytes.Length -ge 256) {
        try {
            try { Add-Type -AssemblyName System.Security.Cryptography.Pkcs -ErrorAction Stop }
            catch { Add-Type -AssemblyName System.Security -ErrorAction Stop }
            $signedCatalog = [Security.Cryptography.Pkcs.SignedCms]::new()
            $signedCatalog.Decode($catalogBytes)
            $signedCatalog.CheckSignature($true)
            $signatureValid = $true
        }
        catch {
        }
    }

    [pscustomobject]@{
        FileName = $entry.FullName
        Capability = $capability.Name
        AllowAny = $authorizedEntities.AllowAny
        AuthorizedPackageFamilies = @($authorizedEntities.SelectNodes("*[local-name()='AuthorizedEntity']") |
            ForEach-Object { $_.AppPackageFamilyName }) -join "; "
        CatalogCharacters = $catalog.Length
        CatalogDecodedBytes = $catalogBytes.Length
        CatalogIsPlaceholder = [string]::IsNullOrWhiteSpace($catalog) -or $catalog -match '^(?:0+|F+)$'
        CatalogSignatureValid = $signatureValid
        DeveloperModeEnabled = $null -ne $developerMode -and $developerMode.AllowDevelopmentWithoutDevLicense -eq 1
    } | Format-List
}

Add-Section "WINDOWS AND DEVICE"
Add-SafeBlock "Windows version registry" {
    Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" |
        Select-Object ProductName, DisplayVersion, EditionID, InstallationType, CurrentBuildNumber, UBR, BuildLabEx |
        Format-List
}
Add-SafeBlock "Operating system" {
    Get-CimInstance Win32_OperatingSystem |
        Select-Object Caption, Version, BuildNumber, OSArchitecture, BootDevice, LastBootUpTime |
        Format-List
}
Add-SafeBlock "MSI hardware identity" {
    Get-CimInstance Win32_ComputerSystem |
        Select-Object Manufacturer, Model, SystemType, SystemFamily |
        Format-List
    Get-CimInstance Win32_BaseBoard |
        Select-Object Manufacturer, Product, Version, SerialNumber |
        Format-List
    Get-CimInstance Win32_BIOS |
        Select-Object Manufacturer, SMBIOSBIOSVersion, ReleaseDate, Version |
        Format-List
}
Add-SafeBlock "Developer and package deployment policies" {
    Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" -ErrorAction SilentlyContinue | Format-List
    Get-ItemProperty "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Appx" -ErrorAction SilentlyContinue | Format-List
}

Add-Section "GAMING FULL SCREEN EXPERIENCE API"
Add-SafeBlock "Gaming FSE exports and active state" {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class TfsXboxDiagnosticsNative {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")]
    public static extern bool FreeLibrary(IntPtr module);
    [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsGamingFullScreenExperienceActive();
}
"@ -ErrorAction Stop
    $module = [TfsXboxDiagnosticsNative]::LoadLibraryW("api-ms-win-gaming-experience-l1-1-0.dll")
    if ($module -eq [IntPtr]::Zero) {
        throw "LoadLibrary failed with Win32 error $([Runtime.InteropServices.Marshal]::GetLastWin32Error())."
    }
    try {
        [pscustomobject]@{
            Module = ("0x{0:X}" -f $module.ToInt64())
            HasIsActiveExport = [TfsXboxDiagnosticsNative]::GetProcAddress($module, "IsGamingFullScreenExperienceActive") -ne [IntPtr]::Zero
            HasSetActiveExport = [TfsXboxDiagnosticsNative]::GetProcAddress($module, "SetGamingFullScreenExperience") -ne [IntPtr]::Zero
            IsActive = [TfsXboxDiagnosticsNative]::IsGamingFullScreenExperienceActive()
        } | Format-List
    }
    finally {
        [void][TfsXboxDiagnosticsNative]::FreeLibrary($module)
    }
}
Add-SafeBlock "Gaming shell file versions" {
    @(
        "$env:WINDIR\System32\twinui.pcshell.dll",
        "$env:WINDIR\System32\Windows.UI.Shell.dll"
    ) | ForEach-Object {
        if (Test-Path -LiteralPath $_) {
            Get-Item -LiteralPath $_ | Select-Object FullName, Length, @{N="FileVersion";E={$_.VersionInfo.FileVersion}}
        }
    } | Format-Table -AutoSize
}

Add-Section "XBOX MODE REGISTRY AND TFS SETTINGS"
Add-SafeBlock "GamingConfiguration" {
    Get-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\GamingConfiguration" -ErrorAction Stop | Format-List
    Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\GamingConfiguration" -ErrorAction Stop | Format-List
}
Add-SafeBlock "TFS Xbox rollback snapshot" {
    Get-ItemProperty "HKCU:\Software\GCM\SteamTools\XboxModeBackup" -ErrorAction Stop | Format-List
}
Add-SafeBlock "Windows shell and TFS autostart" {
    Get-ItemProperty "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon" -ErrorAction SilentlyContinue |
        Select-Object Shell | Format-List
    Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -ErrorAction SilentlyContinue |
        Select-Object TFS, SteamLoader, SteamTools | Format-List
}
Add-SafeBlock "TFS settings JSON" {
    Read-TextFile (Join-Path $InstallRoot "data\tfs.json") 2000
}

Add-Section "REGISTERED APPX PACKAGE"
Add-SafeBlock "Current-user package" {
    $package = Get-AppxPackage -Name $ExpectedPackageName -ErrorAction Stop | Select-Object -First 1
    if (-not $package) { throw "Package '$ExpectedPackageName' is not registered for the current user." }
    $package | Select-Object Name, PackageFullName, PackageFamilyName, Publisher, Version, Architecture, Status, InstallLocation, SignatureKind, IsFramework, IsResourcePackage | Format-List
    Add-Line ("ExpectedFamily: {0}" -f $ExpectedPackageFamilyName)
    Add-Line ("FamilyMatches: {0}" -f ($package.PackageFamilyName -eq $ExpectedPackageFamilyName))
}
Add-SafeBlock "Package manifest gaming extension" {
    $package = Get-AppxPackage -Name $ExpectedPackageName -ErrorAction Stop | Select-Object -First 1
    if (-not $package) { throw "Package is missing." }
    $manifest = Get-AppxPackageManifest -Package $package -ErrorAction Stop
    $manifest.Package.Identity | Format-List
    $manifest.Package.Applications.Application | Format-List Id, Executable, EntryPoint, RuntimeType, StartPage
    $manifest.OuterXml -split "`r?`n" | Where-Object { $_ -match "gamingApp|windows.appExtension|Application Id=|Identity" }
}
Add-SafeBlock "Package data directory" {
    $path = Join-Path $env:LOCALAPPDATA "Packages\$ExpectedPackageFamilyName"
    Get-Item -LiteralPath $path -ErrorAction Stop | Format-List FullName, Attributes, CreationTime, LastWriteTime
    Get-Acl -LiteralPath $path | Format-List
}

Add-Section "CERTIFICATE STORES"
Add-SafeBlock "LocalMachine TrustedPeople certificate" {
    Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction Stop |
        Where-Object Thumbprint -eq $ExpectedThumbprint |
        Select-Object Subject, Issuer, Thumbprint, NotBefore, NotAfter, HasPrivateKey |
        Format-List
}
Add-SafeBlock "CurrentUser TrustedPeople certificate" {
    Get-ChildItem Cert:\CurrentUser\TrustedPeople -ErrorAction Stop |
        Where-Object Thumbprint -eq $ExpectedThumbprint |
        Select-Object Subject, Issuer, Thumbprint, NotBefore, NotAfter, HasPrivateKey |
        Format-List
}
Add-SafeBlock "certutil LocalMachine verification" {
    & "$env:WINDIR\System32\certutil.exe" -store TrustedPeople $ExpectedThumbprint
}

Add-Section "PROCESSES AND TASKS"
Add-SafeBlock "Relevant processes" {
    Get-CimInstance Win32_Process |
        Where-Object { $_.Name -match "ToolsForSteam|XboxHost|steam" } |
        Select-Object ProcessId, ParentProcessId, SessionId, Name, ExecutablePath, CommandLine |
        Sort-Object Name, ProcessId |
        Format-Table -Wrap
}
Add-SafeBlock "TFS scheduled tasks" {
    Get-ScheduledTask -TaskPath "\ToolsForSteam\" -ErrorAction Stop |
        ForEach-Object {
            $info = $_ | Get-ScheduledTaskInfo
            [pscustomobject]@{
                TaskName = $_.TaskName
                State = $_.State
                LastRunTime = $info.LastRunTime
                LastTaskResult = ("0x{0:X8}" -f $info.LastTaskResult)
                Actions = ($_.Actions | ForEach-Object { "$($_.Execute) $($_.Arguments)" }) -join "; "
            }
        } | Format-Table -Wrap
}

Add-SafeBlock "Conflicting logon startup tasks" {
    Get-ScheduledTask -ErrorAction Stop |
        Where-Object {
            $hasLogonTrigger = @($_.Triggers | Where-Object {
                $_.CimClass.CimClassName -eq "MSFT_TaskLogonTrigger"
            }).Count -gt 0
            if (-not $hasLogonTrigger) {
                return $false
            }

            $command = ($_.Actions | ForEach-Object {
                "$($_.Execute) $($_.Arguments)"
            }) -join "; "
            $command -match "(?i)steam(\.exe|://)|toolsforsteam\.exe|steamloader\.exe"
        } |
        ForEach-Object {
            $info = $_ | Get-ScheduledTaskInfo
            [pscustomobject]@{
                TaskPath = $_.TaskPath
                TaskName = $_.TaskName
                State = $_.State
                LastRunTime = $info.LastRunTime
                LastTaskResult = ("0x{0:X8}" -f $info.LastTaskResult)
                Actions = ($_.Actions | ForEach-Object { "$($_.Execute) $($_.Arguments)" }) -join "; "
            }
        } | Format-Table -Wrap
}

Add-SafeBlock "Steam startup coordinator log" {
    $steamStartupLog = Join-Path $InstallRoot "data\steam-startup.log"
    if (Test-Path -LiteralPath $steamStartupLog) {
        Get-Content -LiteralPath $steamStartupLog -Tail 300
    }
    else {
        "Not found: $steamStartupLog"
    }
}

Add-Section "APPX AND SHELL EVENT LOGS (LAST 24 HOURS)"
$eventLogs = @(
    "Microsoft-Windows-AppXDeploymentServer/Operational",
    "Microsoft-Windows-AppModel-Runtime/Admin",
    "Microsoft-Windows-TWinUI/Operational"
)
foreach ($eventLog in $eventLogs) {
    Add-SafeBlock $eventLog {
        $events = @(Get-WinEvent -FilterHashtable @{
            LogName = $eventLog
            StartTime = (Get-Date).AddHours(-24)
        } -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Level -le 3 -or
                $_.Message -match "GCM\.ToolsForSteam|XboxHost|gamingApp|Gaming Full Screen|0x800"
            })
        if ($events.Count -eq 0) {
            "<no matching events>"
            return
        }
        $events |
            Select-Object -First 120 TimeCreated, Id, LevelDisplayName, ProviderName, ActivityId, Message |
            Format-List
    }
}

Add-Section "TOOLS FOR STEAM XBOX LOGS"
foreach ($name in @(
    "xbox-mode-installer.log",
    "xbox-mode.log",
    "installer-update.log",
    "update-handoff.log",
    "steam-startup.log",
    "xbox-startup.log",
    "xbox-host-startup.log",
    "handheld-profile-notifications.log"
)) {
    Add-SafeBlock $name { Read-TextFile (Join-Path $InstallRoot "data\$name") 5000 }
}

Add-Section "INNO SETUP LOG"
Add-SafeBlock "Native installer log" { Read-TextFile $InstallerLogPath 8000 }

Add-Section "END OF REPORT"
Add-Line "Keep this complete file unchanged when sending it for diagnosis."

try {
    [IO.File]::WriteAllText($outputPath, $report.ToString(), [Text.UTF8Encoding]::new($true))
}
catch {
    $emergencyPath = Join-Path $env:TEMP "ToolsForSteam-XboxMode-Diagnostics.log"
    [IO.File]::WriteAllText($emergencyPath, $report.ToString(), [Text.UTF8Encoding]::new($true))
    $outputPath = $emergencyPath
}

if ($InstallRoot) {
    $installedCopy = Join-Path $InstallRoot "data\ToolsForSteam-XboxMode-Diagnostics.log"
    if (-not [string]::Equals($installedCopy, $outputPath, [StringComparison]::OrdinalIgnoreCase)) {
        try {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $installedCopy) | Out-Null
            [IO.File]::WriteAllText($installedCopy, $report.ToString(), [Text.UTF8Encoding]::new($true))
        }
        catch {
        }
    }
}

Write-Output $outputPath
exit 0
