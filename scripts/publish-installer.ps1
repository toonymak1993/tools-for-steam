$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "XboxHostPayloadSnapshot.ps1")
$projectPath = Join-Path $projectRoot "src\SteamLoader.App\SteamLoader.App.csproj"
$innoCandidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
)
$innoPath = $innoCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$issPath = Join-Path $projectRoot "installer\ToolsForSteam.iss"
$installerOutput = Join-Path $projectRoot "dist\installer\ToolsForSteamSetup.exe"
$projectVersion = ([xml](Get-Content $projectPath)).Project.PropertyGroup.Version | Select-Object -First 1
$versionSuffix = if ([string]::IsNullOrWhiteSpace($projectVersion)) { "0.0.0-dev" } else { $projectVersion.Trim() }
$versionedInstallerOutput = Join-Path $projectRoot ("dist\installer\ToolsForSteamSetup-{0}.exe" -f $versionSuffix)
$versionParts = $versionSuffix.Split('.')
$packageMajor = if ($versionParts.Length -ge 1) { [int]$versionParts[0] } else { 0 }
$packageMinor = if ($versionParts.Length -ge 2) { [int]$versionParts[1] } else { 0 }
$packageBuildTime = [DateTimeOffset]::UtcNow
$packageEpoch = [DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
$packageDay = [int][Math]::Floor(($packageBuildTime - $packageEpoch).TotalDays)
$packageTimeSlot = [int][Math]::Floor($packageBuildTime.TimeOfDay.TotalSeconds / 2)
$xboxHostPackageVersion = "$packageMajor.$packageMinor.$packageDay.$packageTimeSlot"

if (-not $innoPath) {
    throw "Inno Setup 6 compiler was not found."
}

if (-not (Test-Path $issPath)) {
    throw "Installer script was not found at $issPath."
}

& (Join-Path $PSScriptRoot "publish-portable.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "Installer payload publish failed."
}

& (Join-Path $PSScriptRoot "prepare-handheld-runtime.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "Handheld replacement runtime preparation failed."
}

$xboxHostPfxPath = if ($env:TFS_XBOX_HOST_PFX) {
    $env:TFS_XBOX_HOST_PFX
} else {
    Join-Path $projectRoot "dist\signing\ToolsForSteam.XboxHost.pfx"
}
$xboxHostPassword = if ($env:TFS_XBOX_HOST_PFX_PASSWORD) {
    $env:TFS_XBOX_HOST_PFX_PASSWORD
} else {
    $passwordFile = Join-Path $projectRoot "dist\signing\ToolsForSteam.XboxHost.password"
    if (Test-Path -LiteralPath $passwordFile) {
        (Get-Content -LiteralPath $passwordFile -Raw).Trim()
    }
}
if (-not (Test-Path -LiteralPath $xboxHostPfxPath) -or [string]::IsNullOrWhiteSpace($xboxHostPassword)) {
    throw "Xbox host signing requires TFS_XBOX_HOST_PFX and TFS_XBOX_HOST_PFX_PASSWORD."
}
$defaultSignedSccdPath = Join-Path $projectRoot "dist\signing\ToolsForSteam.XboxHost.sccd"
$configuredSignedSccdPath = if ($env:TFS_XBOX_HOST_SCCD) {
    $env:TFS_XBOX_HOST_SCCD
} elseif (Test-Path -LiteralPath $defaultSignedSccdPath -PathType Leaf) {
    $defaultSignedSccdPath
}
$xboxHostRequiresDeveloperMode = [string]::IsNullOrWhiteSpace($configuredSignedSccdPath)
$xboxHostSccdPath = if ($xboxHostRequiresDeveloperMode) {
    Join-Path $projectRoot "src\ToolsForSteam.XboxHost\Package\CustomCapability.DeveloperMode.SCCD"
} else {
    $configuredSignedSccdPath
}

$xboxHostBuildParameters = @{
    PfxPath = $xboxHostPfxPath
    PfxPassword = $xboxHostPassword
    SccdPath = $xboxHostSccdPath
    PackageVersion = $xboxHostPackageVersion
}
if ($xboxHostRequiresDeveloperMode) {
    $xboxHostBuildParameters.AllowDeveloperModeSccd = $true
    Write-Host "No Microsoft-signed Gaming Home SCCD was configured; building the disclosed Developer Mode fallback."
}

& (Join-Path $projectRoot "tools\Build-XboxHost.ps1") @xboxHostBuildParameters
if ($LASTEXITCODE -ne 0) {
    throw "Xbox Mode host package build failed."
}

$xboxHostPackagePath = Join-Path $projectRoot "dist\xbox-host\ToolsForSteam.XboxHost.msix"
$xboxHostCertificatePath = Join-Path $projectRoot "dist\xbox-host\ToolsForSteam.XboxHost.cer"
$xboxHostSnapshot = New-XboxHostPayloadSnapshot `
    -ProjectRoot $projectRoot `
    -PackagePath $xboxHostPackagePath `
    -CertificatePath $xboxHostCertificatePath

try {
    $snapshotMetadata = Get-XboxHostPayloadMetadata -PackagePath $xboxHostSnapshot.PackagePath
    if ($snapshotMetadata.Version -ne $xboxHostPackageVersion) {
        throw (
            "Xbox Mode host payload changed before it could be snapshotted. " +
            "Expected $xboxHostPackageVersion, captured $($snapshotMetadata.Version).")
    }
    if ($snapshotMetadata.RequiresDeveloperMode -ne $xboxHostRequiresDeveloperMode) {
        throw "Xbox Mode host SCCD policy changed before it could be snapshotted."
    }

    $installerDirectory = Split-Path -Parent $installerOutput
    if (Test-Path $installerOutput) {
        Remove-Item -LiteralPath $installerOutput -Force
    }

    if (Test-Path $versionedInstallerOutput) {
        Remove-Item -LiteralPath $versionedInstallerOutput -Force
    }

    Remove-Item -LiteralPath (Join-Path $projectRoot "dist\install-toolsforsteam.ps1") -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $projectRoot "dist\uninstall-toolsforsteam.ps1") -Force -ErrorAction SilentlyContinue

    New-Item -ItemType Directory -Path $installerDirectory -Force | Out-Null

    $xboxHostDeveloperModeDefine = if ($xboxHostRequiresDeveloperMode) { 1 } else { 0 }
    & $innoPath `
        "/DXboxHostBuildVersion=$xboxHostPackageVersion" `
        "/DXboxHostRequiresDeveloperMode=$xboxHostDeveloperModeDefine" `
        "/DXboxHostPayloadDir=$($xboxHostSnapshot.Directory)" `
        $issPath
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup build failed."
    }

    if (-not (Test-Path $installerOutput)) {
        throw "Installer output was not created: $installerOutput"
    }

    Copy-Item -LiteralPath $installerOutput -Destination $versionedInstallerOutput -Force
}
finally {
    Remove-XboxHostPayloadSnapshot -ProjectRoot $projectRoot -Snapshot $xboxHostSnapshot
}

Write-Host ""
Write-Host "Installer created:"
Write-Host "  $installerOutput"
Write-Host "Versioned installer created:"
Write-Host "  $versionedInstallerOutput"
