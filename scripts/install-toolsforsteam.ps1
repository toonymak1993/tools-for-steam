param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\ToolsForSteam"),
    [string]$ReleaseZip = "",
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

$repository = "toonymak1993/tools-for-steam"
$assetName = "ToolsForSteam-portable-win-x64.zip"
$projectRoot = Split-Path -Parent $PSScriptRoot
$localZip = Join-Path $projectRoot "dist\$assetName"
$workDir = Join-Path $env:TEMP ("ToolsForSteam-Install-" + [guid]::NewGuid().ToString("N"))
$zipPath = Join-Path $workDir $assetName
$extractDir = Join-Path $workDir "package"

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
    if ($ReleaseZip) {
        Copy-Item -LiteralPath $ReleaseZip -Destination $zipPath -Force
    } elseif (Test-Path $localZip) {
        Copy-Item -LiteralPath $localZip -Destination $zipPath -Force
    } else {
        $downloadUrl = Get-LatestReleaseAssetUrl
        Invoke-WebRequest `
            -Uri $downloadUrl `
            -OutFile $zipPath `
            -Headers @{ "User-Agent" = "ToolsForSteam-Installer" }
    }

    Get-Process ToolsForSteam -ErrorAction SilentlyContinue | Stop-Process -Force

    if (Test-Path $extractDir) {
        Remove-Item -LiteralPath $extractDir -Recurse -Force
    }

    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-Item -Path (Join-Path $extractDir "*") -Destination $InstallDir -Recurse -Force

    $exePath = Join-Path $InstallDir "ToolsForSteam.exe"
    if (-not (Test-Path $exePath)) {
        throw "ToolsForSteam.exe was not found after installation."
    }

    $startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Tools for Steam"
    New-Item -ItemType Directory -Path $startMenuDir -Force | Out-Null

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path $startMenuDir "Tools for Steam.lnk"))
    $shortcut.TargetPath = $exePath
    $shortcut.Arguments = "--manager"
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.IconLocation = $exePath
    $shortcut.Save()

    $uninstallShortcut = $shell.CreateShortcut((Join-Path $startMenuDir "Uninstall Tools for Steam.lnk"))
    $uninstallShortcut.TargetPath = "powershell.exe"
    $uninstallShortcut.Arguments = "-ExecutionPolicy Bypass -File `"$InstallDir\uninstall-toolsforsteam.ps1`""
    $uninstallShortcut.WorkingDirectory = $InstallDir
    $uninstallShortcut.Save()

    Write-Uninstaller -DestinationPath (Join-Path $InstallDir "uninstall-toolsforsteam.ps1")

    Write-Host "Tools for Steam installed to:"
    Write-Host "  $InstallDir"

    if (-not $NoLaunch) {
        Start-Process -FilePath $exePath -ArgumentList "--manager"
    }
} finally {
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}
