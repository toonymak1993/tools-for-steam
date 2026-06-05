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
} catch {
    Write-Warning "Could not restore the Windows shell automatically: $($_.Exception.Message)"
}

try {
    foreach ($valueName in @("TFS", "SteamTools", "SteamLoader")) {
        Remove-ItemProperty -Path $runPath -Name $valueName -ErrorAction SilentlyContinue
    }
} catch {
    Write-Warning "Could not remove all startup entries: $($_.Exception.Message)"
}

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
