param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$Description = "Tools for Steam"
)

# Signs one file with the Certum SimplySign cloud certificate (non-exportable
# key in CurrentUser\My). SimplySign Desktop must be connected in this Windows
# session; it may ask for confirmation. No PIN, token or key is stored here.

$ErrorActionPreference = "Stop"

$thumbprint = "61E90C0AACBF2F407A575903FCC197F45B61706D"
$timestampServer = "http://time.certum.pl"

function Get-SignToolPath {
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    $candidate = Get-ChildItem -LiteralPath $kitsRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
    if (-not $candidate) {
        throw "signtool.exe was not found in the Windows 10/11 SDK."
    }
    return $candidate
}

function Test-SigningKeyAvailable {
    $certificate = Get-ChildItem -LiteralPath "Cert:\CurrentUser\My" -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $thumbprint } |
        Select-Object -First 1
    if (-not $certificate -or -not $certificate.HasPrivateKey) {
        return $false
    }

    $privateKey = $null
    try {
        # HasPrivateKey only proves the association; a stale SimplySign session
        # fails when the key handle is opened.
        $privateKey = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
        return $null -ne $privateKey
    } catch {
        return $false
    } finally {
        if ($null -ne $privateKey) { $privateKey.Dispose() }
    }
}

function Wait-SigningKey {
    $deadline = (Get-Date).AddMinutes(10)
    $announced = $false
    while ((Get-Date) -lt $deadline) {
        if (Test-SigningKeyAvailable) {
            return
        }
        if (-not $announced) {
            Write-Warning "The SimplySign certificate $thumbprint is not available. Connect SimplySign Desktop; signing waits up to 10 minutes."
            Write-Host "TFS_SIGNING_RECONNECT_REQUIRED"
            $announced = $true
        }
        Start-Sleep -Seconds 2
    }
    throw "The SimplySign certificate $thumbprint did not become available."
}

function Assert-Signed([string]$File) {
    $signature = Get-AuthenticodeSignature -LiteralPath $File
    if ($signature.Status -ne "Valid") {
        throw "Invalid signature on ${File}: $($signature.Status) $($signature.StatusMessage)"
    }
    if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $thumbprint) {
        throw "Unexpected signer on $File."
    }
    if (-not $signature.TimeStamperCertificate) {
        throw "Missing RFC3161 timestamp on $File."
    }
}

$resolvedPath = (Resolve-Path -LiteralPath $Path).ProviderPath
$signTool = Get-SignToolPath

for ($attempt = 1; $attempt -le 2; $attempt += 1) {
    Wait-SigningKey
    $unsignedHash = (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash
    & $signTool sign /fd SHA256 /tr $timestampServer /td SHA256 /sha1 $thumbprint /s My /d $Description $resolvedPath
    if ($LASTEXITCODE -eq 0) {
        Assert-Signed $resolvedPath
        Write-Host "Signed: $resolvedPath"
        exit 0
    }

    if ((Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash -ne $unsignedHash) {
        throw "signtool failed and changed $resolvedPath; not retrying."
    }
    Write-Warning "Signing failed (attempt $attempt). Retrying once after SimplySign reconnects."
    Start-Sleep -Seconds 5
}

throw "signtool failed for $resolvedPath."
