$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$targetRoot = Join-Path $projectRoot "dist\handheld-runtime"
$downloadRoot = Join-Path $targetRoot ".downloads"

$artifacts = @(
    [pscustomobject]@{
        Name = "VIIPER v0.7.0 Windows x64"
        Url = "https://github.com/Alia5/VIIPER/releases/download/v0.7.0/viiper-windows-amd64.zip"
        File = "viiper-windows-amd64.zip"
        Sha256 = "A02B06751D64E43E7700ABA8EE1F7E3E4F5F4E7F370A11722FF922AB075C1629"
        Signed = $false
    }
    [pscustomobject]@{
        Name = "VIIPER v0.7.0 corresponding source"
        Url = "https://github.com/Alia5/VIIPER/archive/refs/tags/v0.7.0.zip"
        File = "VIIPER-v0.7.0-source.zip"
        Sha256 = "1DA9FE9EF5CD8791589EF6CDB358E1407761F4644BA54DB4D45F1F560F8F4080"
        Signed = $false
    }
    [pscustomobject]@{
        Name = "USBIP-Win2 v0.9.7.8 x64"
        Url = "https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.7.8/USBip-0.9.7.8-x64.exe"
        File = "USBip-0.9.7.8-x64.exe"
        Sha256 = "44451FE06F4186125C2A5ECD25B099C5560A61A60B1E56F5A0758E77A60AFA44"
        Signed = $true
    }
    [pscustomobject]@{
        Name = "HidHide v1.5.230 x64"
        Url = "https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe"
        File = "HidHide_1.5.230_x64.exe"
        Sha256 = "F4BBBCB82E6258641B887C74BC81C4C5F66E4AA811808DFC304347687B7605F6"
        Signed = $true
    }
)

New-Item -ItemType Directory -Force -Path $downloadRoot | Out-Null

foreach ($artifact in $artifacts) {
    $path = Join-Path $downloadRoot $artifact.File
    $valid = (Test-Path -LiteralPath $path) -and
        ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $artifact.Sha256)
    if (-not $valid) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        Write-Host "Downloading $($artifact.Name)..."
        Invoke-WebRequest -Uri $artifact.Url -OutFile $path
    }

    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($actualHash -ne $artifact.Sha256) {
        throw "$($artifact.Name) failed SHA-256 verification. Expected $($artifact.Sha256), got $actualHash."
    }

    if ($artifact.Signed) {
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
            throw "$($artifact.Name) has no valid Authenticode signature: $($signature.StatusMessage)"
        }
    }
}

$viiperRoot = Join-Path $targetRoot "VIIPER"
$driverRoot = Join-Path $targetRoot "Drivers"
Remove-Item -LiteralPath $viiperRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $driverRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $viiperRoot, $driverRoot | Out-Null

Expand-Archive `
    -LiteralPath (Join-Path $downloadRoot "viiper-windows-amd64.zip") `
    -DestinationPath $viiperRoot `
    -Force
Copy-Item `
    -LiteralPath (Join-Path $downloadRoot "VIIPER-v0.7.0-source.zip") `
    -Destination (Join-Path $viiperRoot "VIIPER-v0.7.0-source.zip")
Copy-Item `
    -LiteralPath (Join-Path $downloadRoot "USBip-0.9.7.8-x64.exe") `
    -Destination (Join-Path $driverRoot "USBip-0.9.7.8-x64.exe")
Copy-Item `
    -LiteralPath (Join-Path $downloadRoot "HidHide_1.5.230_x64.exe") `
    -Destination (Join-Path $driverRoot "HidHide_1.5.230_x64.exe")

$viiperExeHash = (Get-FileHash -LiteralPath (Join-Path $viiperRoot "viiper.exe") -Algorithm SHA256).Hash
if ($viiperExeHash -ne "1868D682F4CC6D62349BBCCBF0727B05D3EB6E22027AC34F0F1D9B1DE56F2DDC") {
    throw "The extracted VIIPER executable failed SHA-256 verification."
}

Write-Host "Handheld replacement runtime prepared at $targetRoot"
