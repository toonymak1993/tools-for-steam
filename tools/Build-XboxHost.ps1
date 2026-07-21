[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PfxPath,

    [Parameter(Mandatory = $true)]
    [string]$PfxPassword,

    [Parameter(Mandatory = $true)]
    [string]$SccdPath,

    [switch]$AllowDeveloperModeSccd,

    [string]$PackageVersion,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\ToolsForSteam.XboxHost\ToolsForSteam.XboxHost.csproj"
$templatePath = Join-Path $repoRoot "src\ToolsForSteam.XboxHost\Package"
$outputRoot = Join-Path $repoRoot "dist\xbox-host"
$publishPath = Join-Path $outputRoot "publish"
$packagePath = Join-Path $outputRoot "package"
$msixPath = Join-Path $outputRoot "ToolsForSteam.XboxHost.msix"
$certificatePath = Join-Path $outputRoot "ToolsForSteam.XboxHost.cer"
$expectedCapabilityName = "Microsoft.appCategory.gamingHome_8wekyb3d8bbwe"
$expectedPackageFamilyName = "GCM.ToolsForSteam.XboxHost_kpg9gzy2ksp2j"

function Get-CertificateSha256 {
    param([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return -join ($sha256.ComputeHash($Certificate.RawData) | ForEach-Object { $_.ToString("x2") })
    }
    finally {
        $sha256.Dispose()
    }
}

function Assert-ProductionSccd {
    param(
        [string]$Path,
        [Security.Cryptography.X509Certificates.X509Certificate2]$PackageCertificate,
        [bool]$AllowDeveloperModeFallback
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "An Xbox Gaming Home SCCD is required at '$Path'."
    }

    try {
        [xml]$sccd = Get-Content -LiteralPath $Path -Raw
    }
    catch {
        throw "The Xbox Gaming Home SCCD is not valid XML: $($_.Exception.Message)"
    }

    $capability = $sccd.SelectSingleNode(
        "/*[local-name()='CustomCapabilityDescriptor']/*[local-name()='CustomCapabilities']/*[local-name()='CustomCapability' and @Name='$expectedCapabilityName']")
    if ($null -eq $capability) {
        throw "The SCCD does not grant the required '$expectedCapabilityName' capability."
    }

    $developerModeOnly = $sccd.SelectSingleNode(
        "/*[local-name()='CustomCapabilityDescriptor']/*[local-name()='DeveloperModeOnly' and translate(@Value, 'TRUE', 'true')='true']")
    if ($null -ne $developerModeOnly) {
        throw "The SCCD is restricted to Developer Mode and cannot be used for a release package."
    }

    $authorizedEntities = $sccd.SelectSingleNode(
        "/*[local-name()='CustomCapabilityDescriptor']/*[local-name()='AuthorizedEntities']")
    if ($null -eq $authorizedEntities) {
        throw "The SCCD does not contain AuthorizedEntities."
    }

    $allowAny = [string]$authorizedEntities.AllowAny -eq "true"
    if (-not $allowAny) {
        $matchingEntities = @($authorizedEntities.SelectNodes(
            "*[local-name()='AuthorizedEntity' and @AppPackageFamilyName='$expectedPackageFamilyName']"))
        if ($matchingEntities.Count -eq 0) {
            throw "The SCCD does not authorize package family '$expectedPackageFamilyName'."
        }

        $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
        try {
            $null = $chain.Build($PackageCertificate)
            $signingHashes = @(
                $PackageCertificate
                $chain.ChainElements | ForEach-Object { $_.Certificate }
            ) | ForEach-Object { Get-CertificateSha256 $_ } | Select-Object -Unique
        }
        finally {
            $chain.Dispose()
        }

        $authorizedHashes = @($matchingEntities | ForEach-Object {
            ([string]$_.CertificateSignatureHash).Trim().ToLowerInvariant()
        })
        if (@($signingHashes | Where-Object { $authorizedHashes -contains $_ }).Count -eq 0) {
            throw "The SCCD does not authorize the certificate chain used to sign the Xbox host package."
        }
    }

    $catalogNode = $sccd.SelectSingleNode(
        "/*[local-name()='CustomCapabilityDescriptor']/*[local-name()='Catalog']")
    $catalog = if ($null -eq $catalogNode) { "" } else { $catalogNode.InnerText.Trim() }
    $isPlaceholderCatalog = [string]::IsNullOrWhiteSpace($catalog) -or $catalog -match '^(?:0+|F+)$'
    if ($isPlaceholderCatalog -and -not $AllowDeveloperModeFallback) {
        throw "The SCCD contains an unsigned placeholder catalog. A Microsoft-signed SCCD is required for non-Developer-Mode devices."
    }
    if ($isPlaceholderCatalog) {
        if (-not $allowAny) {
            throw "A Developer Mode SCCD must use AuthorizedEntities AllowAny=true."
        }
        Write-Host "Xbox host SCCD policy: Windows Developer Mode fallback."
        return
    }

    try {
        $catalogBytes = [Convert]::FromBase64String($catalog)
    }
    catch {
        throw "The SCCD catalog is not valid base64: $($_.Exception.Message)"
    }
    if ($catalogBytes.Length -lt 256) {
        throw "The SCCD catalog is too short to contain a production signature."
    }

    try {
        try {
            Add-Type -AssemblyName System.Security.Cryptography.Pkcs -ErrorAction Stop
        }
        catch {
            Add-Type -AssemblyName System.Security -ErrorAction Stop
        }
        $signedCatalog = [Security.Cryptography.Pkcs.SignedCms]::new()
        $signedCatalog.Decode($catalogBytes)
        $signedCatalog.CheckSignature($true)
    }
    catch {
        throw "The SCCD catalog does not contain a valid cryptographic signature: $($_.Exception.Message)"
    }
}

if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
    $PackageVersion = ([xml](Get-Content -LiteralPath (Join-Path $templatePath "AppxManifest.xml") -Raw)).Package.Identity.Version
}

$parsedPackageVersion = $null
if (-not [version]::TryParse($PackageVersion, [ref]$parsedPackageVersion) -or
    @($parsedPackageVersion.Major, $parsedPackageVersion.Minor, $parsedPackageVersion.Build, $parsedPackageVersion.Revision) |
        Where-Object { $_ -lt 0 -or $_ -gt 65535 }) {
    throw "Invalid MSIX package version '$PackageVersion'. Every component must be between 0 and 65535."
}

$sdkBinCandidates = @()
$windowsKitsBin = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
if (Test-Path -LiteralPath $windowsKitsBin -PathType Container) {
    $sdkBinCandidates += Get-ChildItem -LiteralPath $windowsKitsBin -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64" }
}

$visualStudioSdkPackages = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Shared\NuGetPackages\microsoft.windows.sdk.buildtools"
if (Test-Path -LiteralPath $visualStudioSdkPackages -PathType Container) {
    $sdkBinCandidates += Get-ChildItem -LiteralPath $visualStudioSdkPackages -Directory |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object {
            Get-ChildItem -LiteralPath (Join-Path $_.FullName "bin") -Directory -ErrorAction SilentlyContinue |
                Sort-Object { [version]$_.Name } -Descending |
                ForEach-Object { Join-Path $_.FullName "x64" }
        }
}

$sdkBin = $sdkBinCandidates |
    Where-Object {
        (Test-Path (Join-Path $_ "makeappx.exe") -PathType Leaf) -and
        (Test-Path (Join-Path $_ "signtool.exe") -PathType Leaf)
    } |
    Select-Object -First 1
if (-not $sdkBin) {
    throw "A Windows SDK containing makeappx.exe and signtool.exe is required."
}

$makeAppx = Join-Path $sdkBin "makeappx.exe"
$signTool = Join-Path $sdkBin "signtool.exe"
$resolvedPfxPath = (Resolve-Path -LiteralPath $PfxPath).Path
$resolvedSccdPath = (Resolve-Path -LiteralPath $SccdPath).Path

$validationCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $resolvedPfxPath,
    $PfxPassword,
    [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
try {
    Assert-ProductionSccd `
        -Path $resolvedSccdPath `
        -PackageCertificate $validationCertificate `
        -AllowDeveloperModeFallback $AllowDeveloperModeSccd.IsPresent
}
finally {
    $validationCertificate.Dispose()
}

Remove-Item -LiteralPath $publishPath, $packagePath -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $msixPath, $certificatePath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $publishPath, $packagePath | Out-Null

dotnet publish $projectPath -c $Configuration -r win-x64 --self-contained true -o $publishPath
if ($LASTEXITCODE -ne 0) {
    throw "Xbox host publish failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $templatePath "AppxManifest.xml") -Destination $packagePath
Copy-Item -LiteralPath $resolvedSccdPath -Destination (Join-Path $packagePath "CustomCapability.SCCD")
Copy-Item -LiteralPath (Join-Path $templatePath "Assets") -Destination $packagePath -Recurse
Copy-Item -LiteralPath (Join-Path $templatePath "Public") -Destination $packagePath -Recurse
Copy-Item -LiteralPath (Join-Path $publishPath "ToolsForSteam.XboxHost.exe") -Destination $packagePath

$packagedManifestPath = Join-Path $packagePath "AppxManifest.xml"
$packagedManifest = [xml](Get-Content -LiteralPath $packagedManifestPath -Raw)
$identity = $packagedManifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
if ($null -eq $identity) {
    throw "The Xbox host package manifest does not contain an Identity element."
}
$identity.Version = $PackageVersion
$packagedManifest.Save($packagedManifestPath)

& $makeAppx pack /d $packagePath /p $msixPath /o
if ($LASTEXITCODE -ne 0) {
    throw "MSIX packaging failed with exit code $LASTEXITCODE."
}

& $signTool sign /fd SHA256 /f $resolvedPfxPath /p $PfxPassword $msixPath
if ($LASTEXITCODE -ne 0) {
    throw "MSIX signing failed with exit code $LASTEXITCODE."
}

$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $resolvedPfxPath,
    $PfxPassword,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
try {
    [System.IO.File]::WriteAllBytes(
        $certificatePath,
        $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    Write-Host "Xbox host MSIX: $msixPath"
    Write-Host "Public certificate: $certificatePath"
    Write-Host "Package version: $PackageVersion"
    Write-Host "Certificate thumbprint: $($certificate.Thumbprint)"
}
finally {
    $certificate.Dispose()
}
