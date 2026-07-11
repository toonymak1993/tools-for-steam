[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("VerifyPayload", "InstallAndVerify")]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [string]$CertificatePath,

    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedThumbprint,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedPackageName,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedPackageFamilyName,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedPublisher,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion,

    [Parameter(Mandatory = $true)]
    [string]$LogPath
)

$ErrorActionPreference = "Stop"

function Write-XboxLog {
    param([string]$Message)

    $directory = Split-Path -Parent $LogPath
    if ($directory) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    Add-Content -LiteralPath $LogPath -Value "$(Get-Date -Format o) $Message" -Encoding UTF8
}

function Assert-Value {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }
}

function Get-PackageManifestXml {
    param([string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $archive.GetEntry("AppxManifest.xml")
        Assert-Value ($null -ne $entry) "The MSIX does not contain AppxManifest.xml."
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try {
            return [xml]$reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-Manifest {
    param([xml]$Manifest)

    $identity = $Manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    Assert-Value ($null -ne $identity) "The MSIX identity is missing."
    Assert-Value ($identity.Name -eq $ExpectedPackageName) "Unexpected MSIX package name: $($identity.Name)."
    Assert-Value ($identity.Publisher -eq $ExpectedPublisher) "Unexpected MSIX publisher: $($identity.Publisher)."
    Assert-Value ($identity.Version -eq $ExpectedVersion) "Unexpected MSIX version: $($identity.Version)."

    $application = $Manifest.SelectSingleNode("//*[local-name()='Application' and @Id='App']")
    Assert-Value ($null -ne $application) "The Xbox Mode App application is missing."
    Assert-Value ($application.EntryPoint -eq "Windows.FullTrustApplication") "The Xbox Mode app has an unexpected entry point."

    $gamingExtension = $Manifest.SelectSingleNode(
        "//*[local-name()='Extension' and @Category='windows.appExtension']/*[local-name()='AppExtension' and @Name='windows.gamingApp']")
    Assert-Value ($null -ne $gamingExtension) "The windows.gamingApp extension is missing."
}

function Assert-Payload {
    Assert-Value (Test-Path -LiteralPath $CertificatePath -PathType Leaf) "The Xbox Mode certificate is missing."
    Assert-Value (Test-Path -LiteralPath $PackagePath -PathType Leaf) "The Xbox Mode MSIX is missing."

    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath)
    try {
        Assert-Value ($certificate.Thumbprint -eq $ExpectedThumbprint) "The Xbox Mode certificate thumbprint does not match."
        Assert-Value ($certificate.Subject -eq $ExpectedPublisher) "The Xbox Mode certificate publisher does not match."
        Assert-Value (-not $certificate.HasPrivateKey) "The public installer certificate unexpectedly contains a private key."
        Assert-Value ($certificate.NotAfter -gt (Get-Date).AddDays(30)) "The Xbox Mode certificate is expired or expires within 30 days."
    }
    finally {
        $certificate.Dispose()
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $PackagePath
    Assert-Value ($null -ne $signature.SignerCertificate) "The Xbox Mode MSIX has no readable signer certificate."
    Assert-Value ($signature.SignerCertificate.Thumbprint -eq $ExpectedThumbprint) "The Xbox Mode MSIX signer does not match the bundled certificate."
    Assert-Manifest (Get-PackageManifestXml $PackagePath)
}

function Assert-InstalledPackage {
    $package = Get-AppxPackage -Name $ExpectedPackageName | Select-Object -First 1
    Assert-Value ($null -ne $package) "Windows did not return the registered Xbox Mode package."
    Assert-Value ($package.PackageFamilyName -eq $ExpectedPackageFamilyName) "The registered Xbox Mode package family is unexpected."
    Assert-Value ($package.Version.ToString() -eq $ExpectedVersion) "The registered Xbox Mode package version is unexpected."
    Assert-Value ($package.Status.ToString() -eq "Ok") "The registered Xbox Mode package status is $($package.Status)."

    $installedManifestPath = Join-Path $package.InstallLocation "AppxManifest.xml"
    Assert-Value (Test-Path -LiteralPath $installedManifestPath -PathType Leaf) "The registered Xbox Mode manifest is missing."
    Assert-Manifest ([xml](Get-Content -LiteralPath $installedManifestPath -Raw))
}

try {
    Write-XboxLog "Starting Xbox package operation: $Mode."
    Assert-Payload
    if ($Mode -eq "VerifyPayload") {
        Write-XboxLog "Xbox package payload verification succeeded."
        exit 0
    }

    $previousPackage = Get-AppxPackage -Name $ExpectedPackageName | Select-Object -First 1
    Write-XboxLog $(if ($previousPackage) {
        "Existing package before registration: FullName=$($previousPackage.PackageFullName); Family=$($previousPackage.PackageFamilyName); Version=$($previousPackage.Version); Status=$($previousPackage.Status); InstallLocation=$($previousPackage.InstallLocation)."
    } else {
        "No existing Xbox Mode package was registered for the current user."
    })
    try {
        Get-Process -Name "ToolsForSteam.XboxHost" -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        Write-XboxLog "Stopped running Xbox host processes before package registration."

        $addAppxCommand = Get-Command Add-AppxPackage -ErrorAction Stop
        Assert-Value ($addAppxCommand.Parameters.ContainsKey("Path")) "This Windows Add-AppxPackage command does not expose the required -Path parameter."
        Write-XboxLog "Add-AppxPackage compatibility confirmed: module=$($addAppxCommand.ModuleName); version=$($addAppxCommand.Version); PathParameter=True."
        if ($null -eq $previousPackage -or
            $previousPackage.PackageFamilyName -ne $ExpectedPackageFamilyName -or
            $previousPackage.Version.ToString() -ne $ExpectedVersion -or
            $previousPackage.Status.ToString() -ne "Ok") {
            $registered = $false
            for ($attempt = 1; $attempt -le 3; $attempt++) {
                try {
                    Write-XboxLog "Calling Add-AppxPackage for '$PackagePath' (attempt $attempt of 3)."
                    Add-AppxPackage -Path $PackagePath -ForceApplicationShutdown -ForceUpdateFromAnyVersion -ErrorAction Stop
                    $registered = $true
                    break
                }
                catch {
                    $attemptPackage = Get-AppxPackage -Name $ExpectedPackageName -ErrorAction SilentlyContinue |
                        Select-Object -First 1
                    if ($attemptPackage -and
                        $attemptPackage.PackageFamilyName -eq $ExpectedPackageFamilyName -and
                        $attemptPackage.Version.ToString() -eq $ExpectedVersion -and
                        $attemptPackage.Status.ToString() -eq "Ok") {
                        Write-XboxLog "AppX reported an exception, but the expected package was committed successfully."
                        $registered = $true
                        break
                    }

                    Write-XboxLog "Add-AppxPackage attempt $attempt failed: $($_.Exception.Message)"
                    if ($attempt -ge 3) {
                        throw
                    }

                    # AppXSVC can transiently fail MachineRegisterAdd while reloading deployment extensions.
                    $delaySeconds = if ($attempt -eq 1) { 5 } else { 15 }
                    Write-XboxLog "Waiting $delaySeconds seconds before retrying AppX registration."
                    Start-Sleep -Seconds $delaySeconds
                }
            }

            Assert-Value $registered "Windows did not register the Xbox Mode package after three attempts."
        }
        Assert-InstalledPackage
    }
    catch {
        Write-XboxLog ("Registration exception: " + $_.Exception.ToString())
        Write-XboxLog ("PowerShell error record:`n" + ($_ | Format-List * -Force | Out-String -Width 4096).TrimEnd())
        if ($_.Exception.PSObject.Properties.Name -contains "ActivityId" -and $_.Exception.ActivityId) {
            try {
                Write-XboxLog ("AppX deployment activity log:`n" + (Get-AppPackageLog -ActivityID $_.Exception.ActivityId | Format-List * | Out-String -Width 4096).TrimEnd())
            }
            catch {
                Write-XboxLog "Get-AppPackageLog failed: $($_.Exception.ToString())"
            }
        }
        if ($null -eq $previousPackage) {
            Get-AppxPackage -Name $ExpectedPackageName -ErrorAction SilentlyContinue |
                Remove-AppxPackage -ErrorAction SilentlyContinue
        }
        throw
    }

    Write-XboxLog "Xbox package registration and extension verification succeeded."
    exit 0
}
catch {
    Write-XboxLog "Xbox package operation failed: $($_.Exception.ToString())"
    Write-XboxLog ("PowerShell error record:`n" + ($_ | Format-List * -Force | Out-String -Width 4096).TrimEnd())
    Write-Error $_
    exit 1
}
