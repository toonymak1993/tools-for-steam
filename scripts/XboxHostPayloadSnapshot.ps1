function Get-XboxHostPayloadMetadata {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $packageArchive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $manifestEntry = $packageArchive.GetEntry("AppxManifest.xml")
        if (-not $manifestEntry) {
            throw "Xbox Mode host package does not contain AppxManifest.xml."
        }

        $manifestReader = [System.IO.StreamReader]::new($manifestEntry.Open())
        try {
            [xml]$packageManifest = $manifestReader.ReadToEnd()
        }
        finally {
            $manifestReader.Dispose()
        }

        $sccdEntries = @($packageArchive.Entries | Where-Object {
            $_.FullName -notmatch '[/\\]' -and $_.FullName -match '(?i)\.sccd$'
        })
        if ($sccdEntries.Count -ne 1) {
            throw "Xbox Mode host package must contain exactly one root-level SCCD."
        }

        $sccdReader = [System.IO.StreamReader]::new($sccdEntries[0].Open())
        try {
            [xml]$packageSccd = $sccdReader.ReadToEnd()
        }
        finally {
            $sccdReader.Dispose()
        }
    }
    finally {
        $packageArchive.Dispose()
    }

    $identity = $packageManifest.SelectSingleNode(
        "/*[local-name()='Package']/*[local-name()='Identity']")
    if (-not $identity) {
        throw "Xbox Mode host package manifest does not contain an identity."
    }

    $version = [string]$identity.Version
    if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "Xbox Mode host package has an invalid manifest version: $version"
    }

    $catalogNode = $packageSccd.SelectSingleNode("//*[local-name()='Catalog']")
    $catalog = if ($catalogNode) { $catalogNode.InnerText.Trim() } else { "" }

    [pscustomobject]@{
        Version = $version
        RequiresDeveloperMode = [string]::IsNullOrWhiteSpace($catalog) -or
            $catalog -match '^(?:0+|F+)$'
    }
}

function New-XboxHostPayloadSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectRoot,

        [Parameter(Mandatory = $true)]
        [string]$PackagePath,

        [Parameter(Mandatory = $true)]
        [string]$CertificatePath
    )

    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
        throw "Xbox Mode host package was not found at $PackagePath."
    }
    if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
        throw "Xbox Mode host certificate was not found at $CertificatePath."
    }

    $stagingRoot = Join-Path $ProjectRoot "dist\installer\xbox-payload-staging"
    $snapshotDirectory = Join-Path $stagingRoot ([Guid]::NewGuid().ToString("N"))
    $snapshotPackagePath = Join-Path $snapshotDirectory "ToolsForSteam.XboxHost.msix"
    $snapshotCertificatePath = Join-Path $snapshotDirectory "ToolsForSteam.XboxHost.cer"
    New-Item -ItemType Directory -Path $snapshotDirectory -Force | Out-Null

    try {
        Copy-Item -LiteralPath $PackagePath -Destination $snapshotPackagePath
        Copy-Item -LiteralPath $CertificatePath -Destination $snapshotCertificatePath
    }
    catch {
        Remove-Item -LiteralPath $snapshotPackagePath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $snapshotCertificatePath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $snapshotDirectory -Force -ErrorAction SilentlyContinue
        throw
    }

    [pscustomobject]@{
        Directory = $snapshotDirectory
        PackagePath = $snapshotPackagePath
        CertificatePath = $snapshotCertificatePath
    }
}

function Remove-XboxHostPayloadSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectRoot,

        [Parameter(Mandatory = $true)]
        [psobject]$Snapshot
    )

    $stagingRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $ProjectRoot "dist\installer\xbox-payload-staging"))
    $snapshotDirectory = [System.IO.Path]::GetFullPath([string]$Snapshot.Directory)
    $requiredPrefix = $stagingRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $snapshotDirectory.StartsWith(
            $requiredPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove Xbox payload snapshot outside its staging root: $snapshotDirectory"
    }

    Remove-Item -LiteralPath ([string]$Snapshot.PackagePath) -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath ([string]$Snapshot.CertificatePath) -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $snapshotDirectory -Force -ErrorAction SilentlyContinue
}
