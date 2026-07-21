$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "XboxHostPayloadSnapshot.ps1")
$innoCandidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
)
$innoPath = $innoCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$issPath = Join-Path $projectRoot "installer\ToolsForSteam.iss"
$variantOutputDir = Join-Path $projectRoot "dist\installer\variants"

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

$xboxHostPackagePath = Join-Path $projectRoot "dist\xbox-host\ToolsForSteam.XboxHost.msix"
$xboxHostCertificatePath = Join-Path $projectRoot "dist\xbox-host\ToolsForSteam.XboxHost.cer"
$xboxHostSnapshot = New-XboxHostPayloadSnapshot `
    -ProjectRoot $projectRoot `
    -PackagePath $xboxHostPackagePath `
    -CertificatePath $xboxHostCertificatePath

try {
    $snapshotMetadata = Get-XboxHostPayloadMetadata -PackagePath $xboxHostSnapshot.PackagePath
    $xboxHostPackageVersion = $snapshotMetadata.Version
    $xboxHostRequiresDeveloperMode = $snapshotMetadata.RequiresDeveloperMode
    $xboxHostDeveloperModeDefine = if ($xboxHostRequiresDeveloperMode) { 1 } else { 0 }

New-Item -ItemType Directory -Path $variantOutputDir -Force | Out-Null
Get-ChildItem -LiteralPath $variantOutputDir -Filter '*.exe' -ErrorAction SilentlyContinue | Remove-Item -Force

$variants = @(
    [pscustomobject]@{
        Number = 1
        FileName = "ToolsForSteamSetup-Test1.exe"
        OutputBase = "ToolsForSteamSetup-Test1"
        WizardStyle = "modern"
        CustomPages = 1
        Description = "Full installer, modern wizard, custom pages on, RTSS bootstrap when missing."
    }
    [pscustomobject]@{
        Number = 2
        FileName = "ToolsForSteamSetup-Test2.exe"
        OutputBase = "ToolsForSteamSetup-Test2"
        WizardStyle = "classic"
        CustomPages = 1
        Description = "Full installer, classic wizard, custom pages on, RTSS bootstrap when missing."
    }
    [pscustomobject]@{
        Number = 3
        FileName = "ToolsForSteamSetup-Test3.exe"
        OutputBase = "ToolsForSteamSetup-Test3"
        WizardStyle = "modern"
        CustomPages = 0
        Description = "Modern wizard, no custom pages, shell-mode fallback, RTSS bootstrap when missing."
    }
    [pscustomobject]@{
        Number = 4
        FileName = "ToolsForSteamSetup-Test4.exe"
        OutputBase = "ToolsForSteamSetup-Test4"
        WizardStyle = "classic"
        CustomPages = 0
        Description = "Classic wizard, no custom pages, shell-mode fallback, RTSS bootstrap when missing."
    }
    [pscustomobject]@{
        Number = 5
        FileName = "ToolsForSteamSetup-Test5.exe"
        OutputBase = "ToolsForSteamSetup-Test5"
        WizardStyle = "classic"
        CustomPages = 0
        Description = "Minimal UI installer: classic wizard, no custom pages, RTSS bootstrap when missing."
    }
)

$notes = New-Object System.Collections.Generic.List[string]
$notes.Add("Tools for Steam installer test matrix")
$notes.Add("")

foreach ($variant in $variants) {
    Write-Host ""
    Write-Host ("Building variant {0}: {1}" -f $variant.Number, $variant.Description)

    $arguments = @(
        "/DVariantOutputBaseFilename=$($variant.OutputBase)"
        "/DVariantWizardStyle=$($variant.WizardStyle)"
        "/DVariantCustomPages=$($variant.CustomPages)"
        "/DXboxHostBuildVersion=$xboxHostPackageVersion"
        "/DXboxHostRequiresDeveloperMode=$xboxHostDeveloperModeDefine"
        "/DXboxHostPayloadDir=$($xboxHostSnapshot.Directory)"
        $issPath
    )

    & $innoPath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup build failed for variant $($variant.Number)."
    }

    $builtPath = Join-Path $projectRoot ("dist\installer\{0}.exe" -f $variant.OutputBase)
    if (-not (Test-Path $builtPath)) {
        throw "Variant output was not created: $builtPath"
    }

    $finalPath = Join-Path $variantOutputDir $variant.FileName
    Copy-Item -LiteralPath $builtPath -Destination $finalPath -Force

    $notes.Add(("{0}. {1}" -f $variant.Number, $variant.Description))
    $notes.Add(("   File: {0}" -f $variant.FileName))
}

$notesPath = Join-Path $variantOutputDir "README-variants.txt"
Set-Content -LiteralPath $notesPath -Value $notes -Encoding UTF8

Write-Host ""
Write-Host "Installer variants created:"
foreach ($variant in $variants) {
    Write-Host ("  {0}" -f (Join-Path $variantOutputDir $variant.FileName))
}
Write-Host ("Notes: {0}" -f $notesPath)
}
finally {
    Remove-XboxHostPayloadSnapshot -ProjectRoot $projectRoot -Snapshot $xboxHostSnapshot
}
