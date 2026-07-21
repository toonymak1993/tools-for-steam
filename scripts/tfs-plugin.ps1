[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet("new", "validate", "pack", "sideload")]
    [string]$Command,

    [Parameter(Position = 1)]
    [string]$PluginPath = ".",

    [string]$Id = "my-plugin",

    [string]$Name = "My Plugin",

    [string]$OutputPath = "",

    [string]$RuntimeDataDirectory = "",

    [string]$CommunityCatalogUrl = "https://raw.githubusercontent.com/toonymak1993/tfs-plugin-database/main/catalog.json",

    [string]$CommunityCatalogPath = ""
)

$ErrorActionPreference = "Stop"

$supportedPermissions = @(
    "frontend", "storage", "secrets", "network", "files", "notifications", "logging",
    "native.audio", "native.processes", "native.display", "native.themes", "native.artwork",
    "native.app-start", "native.store-sync", "native.automation", "native.performance", "native.power",
    "native.full-trust"
)

function Resolve-PluginRoot([string]$Value) {
    return [System.IO.Path]::GetFullPath($Value)
}

function Write-Utf8NoBom {
    param(
        [string]$Path,
        [string]$Value
    )

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Value, $encoding)
}

function Test-NetworkHost([string]$HostName) {
    if ($HostName -eq "<local>") {
        return $true
    }

    return $HostName -match '^(\*\.)?[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?$' -and
        $HostName -notmatch '[:/\\]'
}

function Read-AndValidateManifest([string]$Root) {
    $manifestPath = Join-Path $Root "tfs-plugin.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Missing tfs-plugin.json in $Root"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($manifest.id) -or $manifest.id -notmatch '^[a-z0-9][a-z0-9._-]*[a-z0-9]$') {
        throw "Manifest id must be lowercase and use only letters, digits, '.', '_' or '-'."
    }
    if ([string]::IsNullOrWhiteSpace($manifest.name)) {
        throw "Manifest name is required."
    }
    if ([string]::IsNullOrWhiteSpace($manifest.version)) {
        throw "Manifest version is required."
    }
    if ([string]$manifest.sdkVersion -ne "1.0.0") {
        throw "sdkVersion must be 1.0.0 for this Tools for Steam release."
    }

    $entryPoint = [string]$manifest.entryPoint
    if ([string]::IsNullOrWhiteSpace($entryPoint) -or [System.IO.Path]::IsPathRooted($entryPoint) -or $entryPoint -match '(^|[/\\])\.\.([/\\]|$)') {
        throw "entryPoint must be a safe relative JavaScript path."
    }
    $entryPointPath = [System.IO.Path]::GetFullPath((Join-Path $Root $entryPoint))
    if (-not $entryPointPath.StartsWith($Root, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $entryPointPath -PathType Leaf)) {
        throw "Manifest entryPoint does not exist inside the plugin directory."
    }
    $entryPointSource = Get-Content -LiteralPath $entryPointPath -Raw
    if ($entryPointSource -notmatch 'TfsPluginSdk\s*\?*\.\s*register\s*\(') {
        throw "Plugin entryPoint must register through window.TfsPluginSdk.register(manifest, setup)."
    }
    if ($entryPointSource -match '\bsdk\s*\.\s*post\s*\(') {
        throw "sdk.post() is not part of SDK 1.0.0. Use a documented capability such as sdk.network or sdk.storage."
    }

    $permissions = @($manifest.permissions |
        ForEach-Object { ([string]$_).Trim().ToLowerInvariant() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $unknownPermissions = @($permissions | Where-Object { $_ -notin $supportedPermissions })
    if ($unknownPermissions.Count -gt 0) {
        throw "Unsupported permissions: $($unknownPermissions -join ', ')"
    }
    if ($permissions -notcontains "frontend") {
        throw "Community plugins must declare the frontend permission."
    }

    $networkHosts = @($manifest.networkHosts |
        ForEach-Object { ([string]$_).Trim().ToLowerInvariant() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($permissions -contains "network" -and $networkHosts.Count -eq 0) {
        throw "Plugins with network permission must declare networkHosts."
    }
    foreach ($networkHost in $networkHosts) {
        if (-not (Test-NetworkHost $networkHost)) {
            throw "Invalid networkHosts entry: $networkHost"
        }
    }

    if ($null -ne $manifest.backend) {
        if ($permissions -notcontains "native.full-trust") {
            throw "Plugins with backend must declare native.full-trust."
        }
        $backendEntryPoint = [string]$manifest.backend.entryPoint
        if ([string]::IsNullOrWhiteSpace($backendEntryPoint) -or
            [System.IO.Path]::IsPathRooted($backendEntryPoint) -or
            $backendEntryPoint -match '(^|[/\\])\.\.([/\\]|$)') {
            throw "backend.entryPoint must be a safe package-relative path."
        }
        $backendPath = [System.IO.Path]::GetFullPath((Join-Path $Root $backendEntryPoint))
        if (-not $backendPath.StartsWith($Root, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $backendPath -PathType Leaf)) {
            throw "backend.entryPoint does not exist inside the plugin directory."
        }
        $backendRuntime = if ([string]::IsNullOrWhiteSpace($manifest.backend.runtime)) {
            "executable"
        } else {
            ([string]$manifest.backend.runtime).Trim().ToLowerInvariant()
        }
        if ($backendRuntime -notin @("executable", "powershell", "python", "node")) {
            throw "backend.runtime must be executable, powershell, python, or node."
        }
    }

    return $manifest
}

$root = Resolve-PluginRoot $PluginPath

switch ($Command) {
    "new" {
        if (Test-Path -LiteralPath $root) {
            if ((Get-ChildItem -LiteralPath $root -Force -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0) {
                throw "Target plugin directory is not empty: $root"
            }
        }
        New-Item -ItemType Directory -Path (Join-Path $root "dist") -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $root "assets") -Force | Out-Null
        @{
            id = $Id
            name = $Name
            version = "0.1.0"
            sdkVersion = "1.0.0"
            entryPoint = "dist/index.js"
            permissions = @("frontend")
            networkHosts = @()
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root "tfs-plugin.json") -Encoding utf8
        "window.TfsPluginSdk.register({ id: `"$Id`", name: `"$Name`", version: `"0.1.0`", sdkVersion: `"1.0.0`", permissions: [`"frontend`"] }, (sdk) => ({ createScreen: () => sdk.ui.createScreenModel({ title: `"$Name`", slots: [] }) }));" |
            Set-Content -LiteralPath (Join-Path $root "dist\index.js") -Encoding utf8
        Write-Output "Created TFS plugin at $root"
    }
    "validate" {
        $manifest = Read-AndValidateManifest $root
        Write-Output "Valid TFS plugin: $($manifest.id) $($manifest.version)"
    }
    "pack" {
        $manifest = Read-AndValidateManifest $root
        $packageFiles = @(Get-ChildItem -LiteralPath $root -File -Recurse -Force)
        if ($packageFiles.Count -gt 2048) {
            throw "Plugin packages may contain at most 2048 files."
        }
        $oversizedFile = $packageFiles | Where-Object { $_.Length -gt 256MB } | Select-Object -First 1
        if ($oversizedFile) {
            throw "Plugin package file exceeds 256 MB: $($oversizedFile.FullName)"
        }
        $expandedBytes = ($packageFiles | Measure-Object -Property Length -Sum).Sum
        if ($expandedBytes -gt 512MB) {
            throw "Expanded plugin package may not exceed 512 MB."
        }
        if ([string]::IsNullOrWhiteSpace($OutputPath)) {
            $OutputPath = Join-Path (Split-Path $root -Parent) "$($manifest.id)-$($manifest.version).zip"
        }
        $resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
        if (Test-Path -LiteralPath $resolvedOutput) {
            Remove-Item -LiteralPath $resolvedOutput -Force
        }
        Compress-Archive -Path (Join-Path $root "*") -DestinationPath $resolvedOutput -CompressionLevel Optimal
        if ((Get-Item -LiteralPath $resolvedOutput).Length -gt 256MB) {
            Remove-Item -LiteralPath $resolvedOutput -Force
            throw "Compressed plugin package may not exceed 256 MB."
        }
        $hash = (Get-FileHash -LiteralPath $resolvedOutput -Algorithm SHA256).Hash
        Write-Output "Packed $resolvedOutput"
        Write-Output "SHA256 $hash"
    }
    "sideload" {
        $manifest = Read-AndValidateManifest $root
        if ([string]::IsNullOrWhiteSpace($RuntimeDataDirectory)) {
            $installedData = Join-Path $env:LOCALAPPDATA "Programs\ToolsForSteam\data"
            $RuntimeDataDirectory = if (Test-Path -LiteralPath (Split-Path -Parent $installedData)) {
                $installedData
            } else {
                Join-Path (Split-Path -Parent $PSScriptRoot) "src\SteamLoader.App\bin\Debug\net10.0-windows\data"
            }
        }
        $storeRoot = Join-Path ([System.IO.Path]::GetFullPath($RuntimeDataDirectory)) "plugin-store"
        $packagesRoot = Join-Path $storeRoot "packages"
        $imagesRoot = Join-Path $storeRoot "images"
        New-Item -ItemType Directory -Force -Path $packagesRoot, $imagesRoot | Out-Null
        $packageFileName = "$($manifest.id)-$($manifest.version).zip"
        $packagePath = Join-Path $packagesRoot $packageFileName
        & $PSCommandPath pack $root -OutputPath $packagePath
        $hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash

        $imageUrl = $null
        $preview = Get-ChildItem -LiteralPath (Join-Path $root "assets") -File -ErrorAction SilentlyContinue |
            Where-Object { $_.BaseName -eq "preview" -and $_.Extension.ToLowerInvariant() -in @(".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg") } |
            Select-Object -First 1
        if ($preview) {
            $imageFileName = "$($manifest.id)$($preview.Extension.ToLowerInvariant())"
            Copy-Item -LiteralPath $preview.FullName -Destination (Join-Path $imagesRoot $imageFileName) -Force
            $imageUrl = "api/plugin-store/images/catalog/$imageFileName"
        }

        $catalogPath = Join-Path $storeRoot "catalog.json"
        $existingCatalog = $null
        $existingPlugins = @()
        if (Test-Path -LiteralPath $catalogPath) {
            try {
                $existingCatalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
                $existingPlugins = @($existingCatalog.plugins)
            } catch {
                $existingCatalog = $null
                $existingPlugins = @()
            }
        }

        $publicCatalog = $null
        try {
            if (-not [string]::IsNullOrWhiteSpace($CommunityCatalogPath)) {
                $resolvedCommunityCatalogPath = [System.IO.Path]::GetFullPath($CommunityCatalogPath)
                $publicCatalog = Get-Content -LiteralPath $resolvedCommunityCatalogPath -Raw | ConvertFrom-Json
            }
            elseif (-not [string]::IsNullOrWhiteSpace($CommunityCatalogUrl)) {
                $publicCatalog = Invoke-RestMethod -Uri $CommunityCatalogUrl -Method Get -Headers @{
                    "User-Agent" = "ToolsForSteam-Plugin-Sideload/1.0"
                    "Accept" = "application/json"
                } -TimeoutSec 20
            }
        }
        catch {
            Write-Warning "The public community catalog could not be loaded; preserving existing local entries. $($_.Exception.Message)"
            $publicCatalog = $null
        }

        $publicPlugins = @()
        if ($null -ne $publicCatalog) {
            $publicPlugins = @($publicCatalog.plugins | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.id) })
        }
        $publicPluginIds = @($publicPlugins | ForEach-Object { ([string]$_.id).Trim().ToLowerInvariant() })
        $plugins = @($publicPlugins)
        $plugins += @($existingPlugins | Where-Object {
            $existingId = ([string]$_.id).Trim().ToLowerInvariant()
            -not [string]::IsNullOrWhiteSpace($existingId) -and
                $existingId -notin $publicPluginIds -and
                $existingId -ne $manifest.id
        })

        $catalogAuthor = "Local Developer"
        $catalogCategory = "Development"
        $catalogTags = @("development", "sideload")
        $catalogHomepageUrl = ""
        $catalogRepositoryUrl = ""
        $catalogChangelog = "Local sideload build."
        $storeMetadataPath = Join-Path $root "store.json"
        if (Test-Path -LiteralPath $storeMetadataPath -PathType Leaf) {
            $storeMetadata = Get-Content -LiteralPath $storeMetadataPath -Raw | ConvertFrom-Json
            if (-not [string]::IsNullOrWhiteSpace([string]$storeMetadata.author)) {
                $catalogAuthor = [string]$storeMetadata.author
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$storeMetadata.category)) {
                $catalogCategory = [string]$storeMetadata.category
            }
            $metadataTags = @($storeMetadata.tags |
                ForEach-Object { ([string]$_).Trim() } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            if ($metadataTags.Count -gt 0) {
                $catalogTags = $metadataTags
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$storeMetadata.homepageUrl)) {
                $catalogHomepageUrl = [string]$storeMetadata.homepageUrl
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$storeMetadata.repositoryUrl)) {
                $catalogRepositoryUrl = [string]$storeMetadata.repositoryUrl
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$storeMetadata.changelog)) {
                $catalogChangelog = [string]$storeMetadata.changelog
            }
        }

        $manifestNetworkHosts = @($manifest.networkHosts |
            ForEach-Object { ([string]$_).Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $catalogImages = @()
        if (-not [string]::IsNullOrWhiteSpace($imageUrl)) {
            $catalogImages = @($imageUrl)
        }

        $entry = [ordered]@{
            id = $manifest.id
            title = $manifest.name
            description = if ([string]::IsNullOrWhiteSpace($manifest.description)) { "Local development plugin." } else { $manifest.description }
            author = $catalogAuthor
            category = $catalogCategory
            version = $manifest.version
            sdkVersion = $manifest.sdkVersion
            permissions = @($manifest.permissions)
            networkHosts = $manifestNetworkHosts
            packagePath = "./packages/$packageFileName"
            packageSha256 = $hash
            images = $catalogImages
            tags = @($catalogTags)
            changelog = $catalogChangelog
        }
        if (-not [string]::IsNullOrWhiteSpace($catalogHomepageUrl)) {
            $entry.homepageUrl = $catalogHomepageUrl
        }
        if (-not [string]::IsNullOrWhiteSpace($catalogRepositoryUrl)) {
            $entry.repositoryUrl = $catalogRepositoryUrl
        }
        $plugins = @($plugins | Where-Object { $_.id -ne $manifest.id })
        $plugins += [pscustomobject]$entry
        $publicTitle = if ($null -eq $publicCatalog) { "" } else { [string]$publicCatalog.title }
        $publicDescription = if ($null -eq $publicCatalog) { "" } else { [string]$publicCatalog.description }
        $catalog = [ordered]@{
            title = if ([string]::IsNullOrWhiteSpace($publicTitle)) { "TFS Community + Local" } else { "$publicTitle + Local" }
            description = if ([string]::IsNullOrWhiteSpace($publicDescription)) {
                "Public community plugins with local sideloads."
            } else {
                "$publicDescription Local sideloads are included on this device."
            }
            plugins = $plugins
        }
        Write-Utf8NoBom -Path $catalogPath -Value ($catalog | ConvertTo-Json -Depth 20)
        $catalogSource = [ordered]@{
            catalogUrl = $CommunityCatalogUrl
            localDevelopment = $true
        }
        Write-Utf8NoBom `
            -Path (Join-Path $storeRoot "catalog-source.json") `
            -Value ($catalogSource | ConvertTo-Json)
        Write-Output "Sideloaded $($manifest.id) $($manifest.version)"
        Write-Output "Catalog $catalogPath"
        Write-Output "Package $packagePath"
        Write-Output "SHA256 $hash"
        Write-Output "Open the TFS Store and press Refresh, then install from Community."
    }
}
