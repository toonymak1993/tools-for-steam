[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Suspend", "Restore")]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [string]$StatePath,

    [Parameter(Mandatory = $true)]
    [string]$LogPath
)

$ErrorActionPreference = "Stop"
$gamingKeyPath = "Software\Microsoft\Windows\CurrentVersion\GamingConfiguration"

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class TfsGamingFse {
    [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsGamingFullScreenExperienceActive();

    [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true)]
    public static extern int SetGamingFullScreenExperience([MarshalAs(UnmanagedType.Bool)] bool active);
}
"@

function Write-SessionLog {
    param([string]$Message)

    $directory = Split-Path -Parent $LogPath
    if ($directory) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    Add-Content -LiteralPath $LogPath -Value "$(Get-Date -Format o) $Message" -Encoding UTF8
}

function Set-FseState {
    param([bool]$Active)

    $eAbort = -2147467260
    $lastResult = 0
    for ($transitionAttempt = 0; $transitionAttempt -lt 12; $transitionAttempt++) {
        if ([TfsGamingFse]::IsGamingFullScreenExperienceActive() -eq $Active) {
            return
        }

        $lastResult = [TfsGamingFse]::SetGamingFullScreenExperience($Active)
        if ($lastResult -lt 0) {
            if ($lastResult -eq $eAbort) {
                Start-Sleep -Milliseconds 500
                continue
            }
            [Runtime.InteropServices.Marshal]::ThrowExceptionForHR($lastResult)
        }

        for ($confirmationAttempt = 0; $confirmationAttempt -lt 30; $confirmationAttempt++) {
            if ([TfsGamingFse]::IsGamingFullScreenExperienceActive() -eq $Active) {
                return
            }
            Start-Sleep -Milliseconds 100
        }
    }

    if ($lastResult -lt 0) {
        [Runtime.InteropServices.Marshal]::ThrowExceptionForHR($lastResult)
    }
    throw "Windows did not confirm Gaming Full Screen Experience state '$Active'."
}

function Read-RegistryValueState {
    param([Microsoft.Win32.RegistryKey]$Key, [string]$Name)

    $exists = $Key.GetValueNames() -contains $Name
    if (-not $exists) {
        return [pscustomobject]@{ Exists = $false; Value = $null; Kind = "None" }
    }

    return [pscustomobject]@{
        Exists = $true
        Value = $Key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        Kind = $Key.GetValueKind($Name).ToString()
    }
}

function Restore-RegistryValueState {
    param(
        [Microsoft.Win32.RegistryKey]$Key,
        [string]$Name,
        $State
    )

    if ($State.Exists) {
        $kind = [Microsoft.Win32.RegistryValueKind]::$($State.Kind)
        $value = $State.Value
        if ($kind -eq [Microsoft.Win32.RegistryValueKind]::DWord) {
            $value = [int]$value
        }
        $Key.SetValue($Name, $value, $kind)
    } else {
        $Key.DeleteValue($Name, $false)
    }
}

try {
    $stateDirectory = Split-Path -Parent $StatePath
    if ($stateDirectory) {
        New-Item -ItemType Directory -Force -Path $stateDirectory | Out-Null
    }

    if ($Mode -eq "Suspend") {
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($gamingKeyPath)
        try {
            $state = [pscustomobject]@{
                GamingHomeApp = Read-RegistryValueState $key "GamingHomeApp"
                StartupToGamingHome = Read-RegistryValueState $key "StartupToGamingHome"
                SessionWasActive = [TfsGamingFse]::IsGamingFullScreenExperienceActive()
            }
            $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $StatePath -Encoding UTF8
            $key.SetValue("StartupToGamingHome", 0, [Microsoft.Win32.RegistryValueKind]::DWord)
        }
        finally {
            $key.Dispose()
        }

        Set-FseState $false
        Write-SessionLog "Xbox Mode was suspended safely for installer file and package replacement."
        exit 0
    }

    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
        throw "The suspended Xbox Mode state file is missing."
    }

    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($gamingKeyPath)
    try {
        Restore-RegistryValueState $key "GamingHomeApp" $state.GamingHomeApp
        Restore-RegistryValueState $key "StartupToGamingHome" $state.StartupToGamingHome
    }
    finally {
        $key.Dispose()
    }

    if ($state.SessionWasActive) {
        Set-FseState $true
    }
    Write-SessionLog "The Xbox Mode state from before the installer was restored."
    exit 0
}
catch {
    Write-SessionLog "Xbox Mode session operation '$Mode' failed: $($_.Exception.Message)"
    Write-Error $_
    exit 1
}
