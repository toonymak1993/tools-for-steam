param(
    [string]$PluginId = "home-assistant",
    [string]$RepositoryRoot = "",
    [string]$RuntimeDataDirectory = "",
    [string]$ImagePath = "",
    [string]$RepositoryRawBaseUrl = "https://raw.githubusercontent.com/toonymak1993/tools-for-steam/main/sdk"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Resolve-RuntimeDataDirectory {
    param(
        [string]$RepoRoot
    )

    $runningProcess = Get-CimInstance Win32_Process -Filter "name = 'ToolsForSteam.exe'" -ErrorAction SilentlyContinue |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) } |
        Select-Object -First 1

    if ($runningProcess -and (Test-Path -LiteralPath $runningProcess.ExecutablePath)) {
        return (Join-Path (Split-Path -Parent $runningProcess.ExecutablePath) "data")
    }

    return (Join-Path $RepoRoot "src\SteamLoader.App\bin\Debug\net10.0-windows\data")
}

function Get-FileSha256 {
    param(
        [string]$Path
    )

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            return ([System.BitConverter]::ToString($sha256.ComputeHash($stream))).Replace("-", "")
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $sha256.Dispose()
    }
}

function Remove-DirectoryIfSafe {
    param(
        [string]$Path,
        [string]$ExpectedRoot
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedRoot = [System.IO.Path]::GetFullPath($ExpectedRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    if (-not $resolvedPath.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove '$resolvedPath' because it is outside '$resolvedRoot'."
    }

    Remove-Item -LiteralPath $resolvedPath -Recurse -Force
}

$pluginRoot = Join-Path $RepositoryRoot "sdk\official-plugins\$PluginId"
$manifestPath = Join-Path $pluginRoot "tfs-plugin.json"
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Plugin manifest not found: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.id -ne $PluginId) {
    throw "Manifest id '$($manifest.id)' does not match requested plugin id '$PluginId'."
}

$entryPoint = if ([string]::IsNullOrWhiteSpace($manifest.entryPoint)) { "dist/index.js" } else { $manifest.entryPoint }
$entryPointPath = Join-Path $pluginRoot ($entryPoint -replace "/", "\")
if (-not (Test-Path -LiteralPath $entryPointPath)) {
    throw "Plugin entry point not found: $entryPointPath"
}

if ([string]::IsNullOrWhiteSpace($ImagePath) -and $PluginId -eq "home-assistant") {
    $ImagePath = Join-Path $env:USERPROFILE "Downloads\hassio.png"
}

if ([string]::IsNullOrWhiteSpace($ImagePath)) {
    throw "Community plugins require a store image. Pass -ImagePath."
}

if (-not (Test-Path -LiteralPath $ImagePath)) {
    throw "Community plugin image not found: $ImagePath"
}

$imageExtension = [System.IO.Path]::GetExtension($ImagePath).ToLowerInvariant()
if ($imageExtension -notin @(".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg")) {
    throw "Community plugin image must be PNG, JPG, WEBP, GIF, or SVG."
}

if ([string]::IsNullOrWhiteSpace($RuntimeDataDirectory)) {
    $RuntimeDataDirectory = Resolve-RuntimeDataDirectory -RepoRoot $RepositoryRoot
}

$packageRoot = Join-Path $RepositoryRoot "sdk\packages"
$packagePath = Join-Path $packageRoot "$PluginId.zip"
$sdkImagesRoot = Join-Path $RepositoryRoot "sdk\images"
$sdkImageFileName = "$PluginId$imageExtension"
$sdkImagePath = Join-Path $sdkImagesRoot $sdkImageFileName
$tmpRoot = Join-Path $RepositoryRoot ".tmp\community-plugin-build"
$stagingRoot = Join-Path $tmpRoot $PluginId
$storeRoot = Join-Path $RuntimeDataDirectory "plugin-store"
$storePackagesRoot = Join-Path $storeRoot "packages"
$storeImagesRoot = Join-Path $storeRoot "images"
$storePackagePath = Join-Path $storePackagesRoot "$PluginId.zip"
$storeImageFileName = "$PluginId$imageExtension"
$storeImagePath = Join-Path $storeImagesRoot $storeImageFileName
$catalogPath = Join-Path $storeRoot "catalog.json"
$sdkCatalogPath = Join-Path $RepositoryRoot "sdk\catalog.local.json"
$onlineCatalogPath = Join-Path $RepositoryRoot "sdk\catalog.online.json"

New-Item -ItemType Directory -Force -Path $packageRoot, $sdkImagesRoot, $tmpRoot, $storePackagesRoot, $storeImagesRoot | Out-Null
Remove-DirectoryIfSafe -Path $stagingRoot -ExpectedRoot $tmpRoot
New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null

Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $stagingRoot "tfs-plugin.json") -Force

$distSource = Join-Path $pluginRoot "dist"
if (Test-Path -LiteralPath $distSource) {
    Copy-Item -LiteralPath $distSource -Destination (Join-Path $stagingRoot "dist") -Recurse -Force
}

$assetsSource = Join-Path $pluginRoot "assets"
if (Test-Path -LiteralPath $assetsSource) {
    Copy-Item -LiteralPath $assetsSource -Destination (Join-Path $stagingRoot "assets") -Recurse -Force
}

$pluginAssetsRoot = Join-Path $pluginRoot "assets"
New-Item -ItemType Directory -Force -Path $pluginAssetsRoot | Out-Null
Copy-Item -LiteralPath $ImagePath -Destination (Join-Path $pluginAssetsRoot "preview$imageExtension") -Force

$stagingAssetsRoot = Join-Path $stagingRoot "assets"
New-Item -ItemType Directory -Force -Path $stagingAssetsRoot | Out-Null
Copy-Item -LiteralPath $ImagePath -Destination (Join-Path $stagingAssetsRoot "preview$imageExtension") -Force
Copy-Item -LiteralPath $ImagePath -Destination $storeImagePath -Force
Copy-Item -LiteralPath $ImagePath -Destination $sdkImagePath -Force

if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stagingRoot,
    $packagePath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

$sha256 = Get-FileSha256 -Path $packagePath
Copy-Item -LiteralPath $packagePath -Destination $storePackagePath -Force

$catalog = [ordered]@{
    title = "TFS Community"
    description = "Local Tools for Steam community plugin catalog."
    plugins = @(
        [ordered]@{
            id = $manifest.id
            title = $manifest.name
            description = $manifest.description
            author = "Tools for Steam"
            category = "Smart Home"
            version = $manifest.version
            packagePath = "./packages/$PluginId.zip"
            packageSha256 = $sha256
            images = @("api/plugin-store/images/catalog/$storeImageFileName")
            tags = @("smart-home", "lights", "sdk-v1")
            homepageUrl = "https://www.home-assistant.io/"
            repositoryUrl = "https://developers.home-assistant.io/docs/api/rest/"
        }
    )
}

$repositoryRawBaseUrl = $RepositoryRawBaseUrl.TrimEnd("/")
$onlineCatalog = [ordered]@{
    title = "TFS Community"
    description = "Official Tools for Steam community plugin catalog."
    plugins = @(
        [ordered]@{
            id = $manifest.id
            title = $manifest.name
            description = $manifest.description
            author = "Tools for Steam"
            category = "Smart Home"
            version = $manifest.version
            packageUrl = "$repositoryRawBaseUrl/packages/$PluginId.zip"
            packageSha256 = $sha256
            images = @("$repositoryRawBaseUrl/images/$sdkImageFileName")
            tags = @("smart-home", "lights", "sdk-v1")
            homepageUrl = "https://www.home-assistant.io/"
            repositoryUrl = "https://developers.home-assistant.io/docs/api/rest/"
        }
    )
}

$catalogJson = $catalog | ConvertTo-Json -Depth 10
$onlineCatalogJson = $onlineCatalog | ConvertTo-Json -Depth 10
Set-Content -LiteralPath $catalogPath -Value $catalogJson -Encoding UTF8
Set-Content -LiteralPath $sdkCatalogPath -Value $catalogJson -Encoding UTF8
Set-Content -LiteralPath $onlineCatalogPath -Value $onlineCatalogJson -Encoding UTF8

[pscustomobject]@{
    PluginId = $PluginId
    Version = $manifest.version
    PackagePath = $packagePath
    StorePackagePath = $storePackagePath
    PackageSha256 = $sha256
    SdkImagePath = $sdkImagePath
    StoreImagePath = $storeImagePath
    CatalogPath = $catalogPath
    SdkCatalogPath = $sdkCatalogPath
    OnlineCatalogPath = $onlineCatalogPath
    RuntimeDataDirectory = $RuntimeDataDirectory
}
