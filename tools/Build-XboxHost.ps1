[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PfxPath,

    [Parameter(Mandatory = $true)]
    [string]$PfxPassword,

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

if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
    $PackageVersion = ([xml](Get-Content -LiteralPath (Join-Path $templatePath "AppxManifest.xml") -Raw)).Package.Identity.Version
}

$parsedPackageVersion = $null
if (-not [version]::TryParse($PackageVersion, [ref]$parsedPackageVersion) -or
    @($parsedPackageVersion.Major, $parsedPackageVersion.Minor, $parsedPackageVersion.Build, $parsedPackageVersion.Revision) |
        Where-Object { $_ -lt 0 -or $_ -gt 65535 }) {
    throw "Invalid MSIX package version '$PackageVersion'. Every component must be between 0 and 65535."
}

$sdkBin = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName "x64" } |
    Where-Object { Test-Path (Join-Path $_ "makeappx.exe") -PathType Leaf } |
    Select-Object -First 1
if (-not $sdkBin) {
    throw "A Windows SDK containing makeappx.exe and signtool.exe is required."
}

$makeAppx = Join-Path $sdkBin "makeappx.exe"
$signTool = Join-Path $sdkBin "signtool.exe"
$resolvedPfxPath = (Resolve-Path -LiteralPath $PfxPath).Path

Remove-Item -LiteralPath $publishPath, $packagePath -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $msixPath, $certificatePath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $publishPath, $packagePath | Out-Null

dotnet publish $projectPath -c $Configuration -r win-x64 --self-contained true -o $publishPath
if ($LASTEXITCODE -ne 0) {
    throw "Xbox host publish failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $templatePath "AppxManifest.xml") -Destination $packagePath
Copy-Item -LiteralPath (Join-Path $templatePath "CustomCapability.SCCD") -Destination $packagePath
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
