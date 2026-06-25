param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\ToolsForSteam"),
    [string]$ReleaseInstaller = "",
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

$repository = "toonymak1993/tools-for-steam"
$assetName = "ToolsForSteamSetup.exe"
$projectRoot = Split-Path -Parent $PSScriptRoot
$localInstaller = Join-Path $projectRoot "dist\installer\$assetName"
$workDir = Join-Path $env:TEMP ("ToolsForSteam-Install-" + [guid]::NewGuid().ToString("N"))
$installerPath = Join-Path $workDir $assetName

function Write-Uninstaller {
    param([string]$DestinationPath)

    $sourcePath = Join-Path $PSScriptRoot "uninstall-toolsforsteam.ps1"
    if (Test-Path $sourcePath) {
        Copy-Item -LiteralPath $sourcePath -Destination $DestinationPath -Force
        return
    }

@'
param(
    [string]$InstallDir = $PSScriptRoot
)

$ErrorActionPreference = "Stop"

$winlogonPath = "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon"
$runPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Tools for Steam"

Get-Process ToolsForSteam -ErrorAction SilentlyContinue | Stop-Process -Force

try {
    $shell = (Get-ItemProperty -Path $winlogonPath -Name Shell -ErrorAction SilentlyContinue).Shell
    if ($shell -and $shell.IndexOf("ToolsForSteam.exe", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        Set-ItemProperty -Path $winlogonPath -Name Shell -Value "explorer.exe"
    }
} catch {}

try {
    foreach ($valueName in @("TFS", "SteamTools", "SteamLoader")) {
        Remove-ItemProperty -Path $runPath -Name $valueName -ErrorAction SilentlyContinue
    }
} catch {}

Remove-Item -LiteralPath $startMenuDir -Recurse -Force -ErrorAction SilentlyContinue

if (Test-Path $InstallDir) {
    $cleanupScript = Join-Path $env:TEMP ("ToolsForSteam-Uninstall-" + [guid]::NewGuid().ToString("N") + ".ps1")
    @"
Start-Sleep -Seconds 2
Remove-Item -LiteralPath "$InstallDir" -Recurse -Force -ErrorAction SilentlyContinue
"@ | Set-Content -Path $cleanupScript

    Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$cleanupScript`"" -WindowStyle Hidden
}

Write-Host "Tools for Steam was removed. Windows shell is set back to explorer.exe."
'@ | Set-Content -Path $DestinationPath
}

function Get-LatestReleaseAssetUrl {
    $release = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$repository/releases/latest" `
        -Headers @{ "User-Agent" = "ToolsForSteam-Installer" }

    $asset = $release.assets | Where-Object { $_.name -ieq $assetName } | Select-Object -First 1
    if (-not $asset) {
        throw "The latest GitHub release does not contain $assetName."
    }

    return $asset.browser_download_url
}

New-Item -ItemType Directory -Path $workDir -Force | Out-Null

try {
    if ($ReleaseInstaller) {
        Copy-Item -LiteralPath $ReleaseInstaller -Destination $installerPath -Force
    } elseif (Test-Path $localInstaller) {
        Copy-Item -LiteralPath $localInstaller -Destination $installerPath -Force
    } else {
        $downloadUrl = Get-LatestReleaseAssetUrl
        Invoke-WebRequest `
            -Uri $downloadUrl `
            -OutFile $installerPath `
            -Headers @{ "User-Agent" = "ToolsForSteam-Installer" }
    }

    Get-Process ToolsForSteam -ErrorAction SilentlyContinue | Stop-Process -Force

    $arguments = @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART",
        "/DIR=`"$InstallDir`""
    )

    $installer = Start-Process -FilePath $installerPath -ArgumentList $arguments -PassThru -Wait
    if ($installer.ExitCode -ne 0) {
        throw "The installer exited with code $($installer.ExitCode)."
    }

    Write-Host "Tools for Steam installer finished for:"
    Write-Host "  $InstallDir"
} finally {
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}
