$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot "src\SteamLoader.App\SteamLoader.App.csproj"
$distRoot = Join-Path $projectRoot "dist"
$portableRoot = Join-Path $distRoot "portable"

if (Test-Path $portableRoot) {
    Remove-Item -LiteralPath $portableRoot -Recurse -Force
}

Get-ChildItem -Path $distRoot -Filter "ToolsForSteam-portable-win-x64*.zip" -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem -Path $distRoot -Filter "SteamTools-portable-win-x64*.zip" -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem -Path $distRoot -Filter "SteamLoader-portable-win-x64*.zip" -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

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
$discordNoticesRoot = Join-Path $portableRoot "ThirdParty\DiscordSocialSdk"
New-Item -ItemType Directory -Path $discordNoticesRoot -Force | Out-Null
Copy-Item `
    -LiteralPath (Join-Path $projectRoot "src\SteamLoader.App\ThirdParty\DiscordSocialSdk\License-Notices.txt") `
    -Destination (Join-Path $discordNoticesRoot "License-Notices.txt")
Copy-Item `
    -LiteralPath (Join-Path $projectRoot "src\SteamLoader.App\ThirdParty\DiscordSocialSdk\NOTICE.txt") `
    -Destination (Join-Path $discordNoticesRoot "NOTICE.txt")

Write-Host ""
Write-Host "Installer payload prepared:"
Write-Host "  $portableRoot"
