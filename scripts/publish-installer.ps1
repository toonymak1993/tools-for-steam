$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot "src\SteamLoader.App\SteamLoader.App.csproj"
$innoPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
$issPath = Join-Path $projectRoot "installer\ToolsForSteam.iss"
$installerOutput = Join-Path $projectRoot "dist\installer\ToolsForSteamSetup.exe"
$projectVersion = ([xml](Get-Content $projectPath)).Project.PropertyGroup.Version | Select-Object -First 1
$versionSuffix = if ([string]::IsNullOrWhiteSpace($projectVersion)) { "0.0.0-dev" } else { $projectVersion.Trim() }
$versionedInstallerOutput = Join-Path $projectRoot ("dist\installer\ToolsForSteamSetup-{0}.exe" -f $versionSuffix)

if (-not (Test-Path $innoPath)) {
    throw "Inno Setup compiler was not found at $innoPath."
}

if (-not (Test-Path $issPath)) {
    throw "Installer script was not found at $issPath."
}

& (Join-Path $PSScriptRoot "publish-portable.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "Installer payload publish failed."
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

& $innoPath $issPath
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup build failed."
}

if (-not (Test-Path $installerOutput)) {
    throw "Installer output was not created: $installerOutput"
}

Copy-Item -LiteralPath $installerOutput -Destination $versionedInstallerOutput -Force

Write-Host ""
Write-Host "Installer created:"
Write-Host "  $installerOutput"
Write-Host "Versioned installer created:"
Write-Host "  $versionedInstallerOutput"
