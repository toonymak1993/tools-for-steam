$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$innoPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
$issPath = Join-Path $projectRoot "installer\ToolsForSteam.iss"
$installerOutput = Join-Path $projectRoot "dist\installer\ToolsForSteamSetup.exe"

if (-not (Test-Path $innoPath)) {
    throw "Inno Setup compiler was not found at $innoPath."
}

if (-not (Test-Path $issPath)) {
    throw "Installer script was not found at $issPath."
}

& (Join-Path $PSScriptRoot "publish-portable.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "Portable publish failed."
}

$installerDirectory = Split-Path -Parent $installerOutput
if (Test-Path $installerOutput) {
    Remove-Item -LiteralPath $installerOutput -Force
}

Remove-Item -LiteralPath (Join-Path $projectRoot "dist\install-toolsforsteam.ps1") -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $projectRoot "dist\uninstall-toolsforsteam.ps1") -Force -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Path $installerDirectory -Force | Out-Null

& $innoPath $issPath
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup build failed."
}

if (-not (Test-Path $installerOutput)) {
    throw "Installer output was not created: $installerOutput"
}

Write-Host ""
Write-Host "Installer created:"
Write-Host "  $installerOutput"
