param(
    # Builds without the Certum signature (local tests only, never for a release).
    [switch]$Unsigned
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot "src\SteamLoader.App\SteamLoader.App.csproj"
$innoCandidates = @(
    "C:\Program Files\Inno Setup 7\ISCC.exe"
    "C:\Program Files (x86)\Inno Setup 7\ISCC.exe"
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe")
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    "C:\Program Files\Inno Setup 6\ISCC.exe"
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
)
$innoPath = $innoCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$issPath = Join-Path $projectRoot "installer\ToolsForSteam.iss"
$installerOutput = Join-Path $projectRoot "dist\installer\ToolsForSteamSetup.exe"
$projectVersion = ([xml](Get-Content $projectPath)).Project.PropertyGroup.Version | Select-Object -First 1
$versionSuffix = if ([string]::IsNullOrWhiteSpace($projectVersion)) { "0.0.0-dev" } else { $projectVersion.Trim() }
$versionedInstallerOutput = Join-Path $projectRoot ("dist\installer\ToolsForSteamSetup-{0}.exe" -f $versionSuffix)
$signFileScript = Join-Path $PSScriptRoot "sign-file.ps1"
$windowsPowerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$signFileCommand = "$windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $signFileScript -Path"

if (-not $innoPath) {
    throw "Inno Setup 7 or 6 compiler was not found."
}

if (-not (Test-Path $issPath)) {
    throw "Installer script was not found at $issPath."
}

$issVersion = Select-String -LiteralPath $issPath -Pattern '^#define MyAppVersion "([^"]+)"' |
    Select-Object -First 1 |
    ForEach-Object { $_.Matches[0].Groups[1].Value }
if ($issVersion -ne $versionSuffix) {
    throw "Installer version $issVersion does not match the app version $versionSuffix."
}

if (-not $Unsigned -and ($signFileScript.Contains(" ") -or $windowsPowerShell.Contains(" "))) {
    # ISCC's /S tool definition cannot carry quoted paths through PowerShell.
    throw "The signing tool paths must not contain spaces: $signFileScript"
}

& (Join-Path $PSScriptRoot "publish-portable.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "Installer payload publish failed."
}

& (Join-Path $PSScriptRoot "prepare-handheld-runtime.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "Handheld replacement runtime preparation failed."
}

if (-not $Unsigned) {
    # The installer copies this exe to {app} and also embeds it as the update helper.
    & $signFileScript -Path (Join-Path $projectRoot "dist\portable\ToolsForSteam.exe")
    if ($LASTEXITCODE -ne 0) {
        throw "Signing ToolsForSteam.exe failed."
    }
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

$isccArguments = @()
if (-not $Unsigned) {
    # Inno signs the uninstaller and the finished setup through this tool.
    $isccArguments += "/DTfsSign=1"
    $isccArguments += ('/Stfssign=' + $signFileCommand + ' $f')
}
$isccArguments += $issPath

& $innoPath @isccArguments
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup build failed."
}

if (-not (Test-Path $installerOutput)) {
    throw "Installer output was not created: $installerOutput"
}

if (-not $Unsigned) {
    $signature = Get-AuthenticodeSignature -LiteralPath $installerOutput
    if ($signature.Status -ne "Valid" -or
        $signature.SignerCertificate.Thumbprint -ne "61E90C0AACBF2F407A575903FCC197F45B61706D" -or
        -not $signature.TimeStamperCertificate) {
        throw "The installer is not correctly signed: $($signature.Status) $($signature.StatusMessage)"
    }
}

Copy-Item -LiteralPath $installerOutput -Destination $versionedInstallerOutput -Force

Write-Host ""
Write-Host "Installer created:"
Write-Host "  $installerOutput"
Write-Host "Versioned installer created:"
Write-Host "  $versionedInstallerOutput"
Write-Host ("SHA-256: {0}" -f (Get-FileHash -LiteralPath $installerOutput -Algorithm SHA256).Hash)
