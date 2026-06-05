$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot "src\SteamLoader.App\SteamLoader.App.csproj"
$distRoot = Join-Path $projectRoot "dist"
$portableRoot = Join-Path $distRoot "portable"
$zipPath = Join-Path $distRoot "ToolsForSteam-portable-win-x64.zip"
$legacyZipPath = Join-Path $distRoot "SteamTools-portable-win-x64.zip"
$legacySteamLoaderZipPath = Join-Path $distRoot "SteamLoader-portable-win-x64.zip"

if (Test-Path $portableRoot) {
    Remove-Item -LiteralPath $portableRoot -Recurse -Force
}

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

if (Test-Path $legacyZipPath) {
    Remove-Item -LiteralPath $legacyZipPath -Force
}

if (Test-Path $legacySteamLoaderZipPath) {
    Remove-Item -LiteralPath $legacySteamLoaderZipPath -Force
}

New-Item -ItemType Directory -Path $portableRoot -Force | Out-Null

& dotnet restore $projectPath -r win-x64
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed."
}

& dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --no-restore `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $portableRoot

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

Copy-Item -LiteralPath (Join-Path $projectRoot "README.md") -Destination (Join-Path $portableRoot "README.md")
Copy-Item -LiteralPath (Join-Path $projectRoot "LICENSE.txt") -Destination (Join-Path $portableRoot "LICENSE.txt")

Compress-Archive -Path (Join-Path $portableRoot "*") -DestinationPath $zipPath -Force

Write-Host ""
Write-Host "Portable build created:"
Write-Host "  $portableRoot"
Write-Host "ZIP package:"
Write-Host "  $zipPath"
