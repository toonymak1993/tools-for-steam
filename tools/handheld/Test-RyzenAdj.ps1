<#
  Test-RyzenAdj.ps1
  Machbarkeits-Check fuer das geplante "handheld"-Plugin:
  Kann RyzenAdj den Ryzen Z2 Extreme (MSI Claw A8) ueberhaupt auslesen und setzen?

  Ablauf:
    1) Liest die aktuellen Power-Limits aus  (ryzenadj -i)   <-- WICHTIGSTES Kriterium
    2) Setzt testweise ein NIEDRIGES, sicheres Profil (SPL 15W / SPPT 17W / FPPT 20W)
    3) Liest erneut aus, damit man sieht ob das Setzen greift
    4) Schreibt alles nach handheld-ryzenadj-test.log

  Sicherheit:
    - Muss als Administrator laufen (das Skript startet sich noetigenfalls selbst erhoeht neu).
    - Es werden nur NIEDRIGE Werte gesetzt -> keine Ueberhitzungsgefahr.
    - Ein Neustart stellt die MSI-Standardwerte wieder her.
    - Aendert dauerhaft nichts, installiert keinen Dienst, laedt nichts herunter.

  Voraussetzung:
    RyzenAdj (win64) von https://github.com/FlyGoat/RyzenAdj/releases herunterladen und
    entpacken. ryzenadj.exe, libryzenadj.dll und WinRing0x64.sys MUESSEN im selben Ordner liegen.
    Dieses Skript am einfachsten in genau diesen Ordner legen und starten.
#>

param(
    # Ordner mit ryzenadj.exe (+ libryzenadj.dll + WinRing0x64.sys). Standard: Ordner dieses Skripts.
    [string]$RyzenAdjDir = $PSScriptRoot,
    # Nur auslesen, nichts setzen.
    [switch]$SkipSetTest
)

$ErrorActionPreference = 'Continue'

# --- Als Administrator sicherstellen (self-elevate) ---
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Write-Host "Starte als Administrator neu..." -ForegroundColor Yellow
    $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"",'-RyzenAdjDir',"`"$RyzenAdjDir`"")
    if ($SkipSetTest) { $argList += '-SkipSetTest' }
    try { Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList } catch { Write-Host "Konnte nicht erhoehen: $($_.Exception.Message)" -ForegroundColor Red }
    return
}

$exe = Join-Path $RyzenAdjDir 'ryzenadj.exe'
$log = Join-Path $PSScriptRoot 'handheld-ryzenadj-test.log'

function Log($msg) {
    $line = "{0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg
    Write-Host $line
    Add-Content -Path $log -Value $line
}

Set-Content -Path $log -Value "" -ErrorAction SilentlyContinue
Log "=== RyzenAdj Machbarkeits-Test (handheld-Plugin) ==="
Log "RyzenAdj-Ordner: $RyzenAdjDir"

# CPU zur Einordnung (erwartet: AMD Ryzen ... Z2 Extreme)
try { Log ("CPU: " + (Get-CimInstance Win32_Processor -ErrorAction Stop).Name) } catch { Log "CPU: (nicht lesbar)" }

# Memory Integrity / HVCI (kann WinRing0 blockieren)
try {
    $hvci = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' -ErrorAction Stop).Enabled
    Log ("Memory Integrity (HVCI): " + $(if ($hvci -eq 1) { 'AN  (kann WinRing0 blockieren!)' } else { 'aus' }))
} catch { Log "Memory Integrity (HVCI): unbekannt" }

if (-not (Test-Path $exe)) {
    Log ""
    Log "FEHLER: ryzenadj.exe nicht gefunden unter: $exe"
    Log "-> RyzenAdj (win64) von https://github.com/FlyGoat/RyzenAdj/releases entpacken"
    Log "   und ryzenadj.exe + libryzenadj.dll + WinRing0x64.sys in diesen Ordner legen (oder -RyzenAdjDir angeben)."
    Read-Host "Enter zum Beenden"
    return
}

# --- Schritt 1: auslesen (wichtigstes Kriterium) ---
Log ""
Log "--- Schritt 1: aktuelle Werte auslesen (ryzenadj -i) ---"
$before = (& $exe -i 2>&1 | Out-String)
Log $before

if ($before -match 'not supported|unsupported|Unable to|unable to|failed|error|0\.000') {
    Log "HINWEIS: Ausgabe wirkt problematisch. Moegliche Ursachen:"
    Log "  * Treiber laedt nicht (Memory Integrity blockiert WinRing0) -> dann PawnIO-Weg noetig"
    Log "  * Modell 0x1A24 (Z2 Extreme) noch nicht erkannt -> gepatchter RyzenAdj-Build noetig"
}

# --- Schritt 2: sicheres niedriges Profil setzen ---
if (-not $SkipSetTest) {
    Log ""
    Log "--- Schritt 2: TEST-Profil setzen (SPL 15W / SPPT 17W / FPPT 20W) ---"
    $set = (& $exe --stapm-limit=15000 --slow-limit=17000 --fast-limit=20000 2>&1 | Out-String)
    Log $set
    Start-Sleep -Seconds 2
    Log "--- erneut auslesen ---"
    $after = (& $exe -i 2>&1 | Out-String)
    Log $after
    Log "Deutung: Aendern sich STAPM/PPT gegenueber Schritt 1 -> Setzen funktioniert."
    Log "Falls die Werte weiter auf deiner MSI-Einstellung stehen, ueberschreibt MSI Center M sofort wieder"
    Log "(das loesen wir spaeter mit einem Re-Apply-Timer). Entscheidend ist erstmal, dass Schritt 1 ECHTE Werte zeigt."
    Log "Ein Neustart stellt die MSI-Standardwerte wieder her."
}

Log ""
Log "=== FERTIG ==="
Log "Bitte schick mir den kompletten Inhalt dieser Datei:"
Log $log
Read-Host "Enter zum Beenden"
