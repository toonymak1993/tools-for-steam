<#
  Test-MSI-WMI.ps1
  Treiberfreier Machbarkeits-Check fuer das geplante "handheld"-Plugin.

  Frage: Ist MSIs eigene WMI/ACPI-Schnittstelle (Klasse MSI_ACPI in root\WMI) auf dem
  MSI Claw A8 vorhanden und aufrufbar? Genau ueber diese steuern MSI Center M UND
  Handheld Companion die Power-Limits (SPL/SPPT/FPPT) - treiberfrei, direkt ins EC.

  Dieses Skript ist NUR-LESEND und sicher:
    - listet MSI_ACPI + dessen Methoden auf (Get_EC/Set_EC/Get_Power/Set_Power/Get_Fan ...)
    - ruft best effort NUR Get_*-Methoden auf (Interface-Version, EC-Info, Luefter, Power)
    - fuehrt KEINE Set_*-Aufrufe aus, schreibt nichts, laedt keinen Treiber
  Alles wird nach handheld-msi-wmi-test.log geschrieben.

  Als Administrator ausfuehren (WMI-Methodenaufrufe brauchen meist erhoehte Rechte).
  Das Skript startet sich noetigenfalls selbst erhoeht neu.
#>

param([switch]$SkipInvoke)

# --- Als Administrator sicherstellen ---
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Write-Host "Starte als Administrator neu..." -ForegroundColor Yellow
    $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"")
    if ($SkipInvoke) { $argList += '-SkipInvoke' }
    try { Start-Process powershell.exe -Verb RunAs -ArgumentList $argList } catch { Write-Host "Erhoehung fehlgeschlagen: $($_.Exception.Message)" -ForegroundColor Red }
    return
}

$log = Join-Path $PSScriptRoot 'handheld-msi-wmi-test.log'
function Log($m) { $l = "{0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $m; Write-Host $l; Add-Content -Path $log -Value $l }
Set-Content -Path $log -Value "" -ErrorAction SilentlyContinue

Log "=== MSI WMI Machbarkeits-Test (handheld-Plugin) ==="
try { Log ("CPU:    " + (Get-CimInstance Win32_Processor -ErrorAction Stop).Name) } catch {}
try { Log ("Modell: " + (Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).Model) } catch {}

# --- Schritt 1: MSI-/Package-Klassen in root\WMI ---
Log ""
Log "--- Schritt 1: relevante Klassen in root\WMI ---"
$classNames = @()
try {
    $classNames = Get-CimClass -Namespace 'root/WMI' -ErrorAction Stop |
        Where-Object { $_.CimClassName -like 'MSI*' -or $_.CimClassName -like 'Package*' } |
        Select-Object -ExpandProperty CimClassName
    if ($classNames) { $classNames | Sort-Object | ForEach-Object { Log "  Klasse: $_" } }
    else { Log "  KEINE MSI*/Package-Klassen gefunden." }
} catch { Log "  Fehler beim Auflisten: $($_.Exception.Message)" }

$hasAcpi = $classNames -contains 'MSI_ACPI'
Log ("=> MSI_ACPI vorhanden: " + $(if ($hasAcpi) { 'JA' } else { 'NEIN' }))

# --- Schritt 2: Methoden von MSI_ACPI ---
if ($hasAcpi) {
    Log ""
    Log "--- Schritt 2: Methoden von MSI_ACPI ---"
    try {
        (Get-CimClass -Namespace 'root/WMI' -ClassName 'MSI_ACPI').CimClassMethods |
            ForEach-Object { Log ("  Methode: " + $_.Name) }
    } catch { Log "  Fehler: $($_.Exception.Message)" }
    try {
        $count = @(Get-WmiObject -Namespace 'root\WMI' -Class 'MSI_ACPI' -ErrorAction Stop).Count
        Log "  Instanzen: $count"
    } catch { Log "  Instanzen: Fehler $($_.Exception.Message)" }
}

# --- Schritt 3: NUR-LESENDE Testaufrufe (best effort) ---
if ($hasAcpi -and -not $SkipInvoke) {
    Log ""
    Log "--- Schritt 3: nur-lesende Testaufrufe (KEINE Set_-Aufrufe!) ---"
    try {
        $msi = Get-WmiObject -Namespace 'root\WMI' -Class 'MSI_ACPI' -ErrorAction Stop | Select-Object -First 1
        $pkgClass = New-Object System.Management.ManagementClass('\\.\root\WMI:Package_32')

        function Invoke-Read([string]$method, [int]$sub) {
            try {
                $in = $pkgClass.CreateInstance()
                $b = New-Object byte[] 32
                $b[0] = [byte]$sub
                $in.Bytes = $b
                $res = $msi.InvokeMethod($method, @([object]$in))
                $out = $null
                if ($res -is [byte[]]) { $out = $res }
                elseif ($res.Data -and $res.Data.Bytes) { $out = $res.Data.Bytes }
                elseif ($res.Bytes) { $out = $res.Bytes }
                if ($out) {
                    $hex = ($out | ForEach-Object { $_.ToString('X2') }) -join ' '
                    Log ("  {0,-12} OK  -> {1}" -f $method, $hex)
                } else {
                    Log ("  {0,-12} aufgerufen (Ausgabe nicht direkt lesbar, Typ: {1})" -f $method, $res.GetType().Name)
                }
            } catch {
                Log ("  {0,-12} Fehler: {1}" -f $method, $_.Exception.Message)
            }
        }

        Invoke-Read 'Get_WMI'   0   # Interface-Version
        Invoke-Read 'Get_EC'    0   # EC-Info / Firmware
        Invoke-Read 'Get_Fan'   0   # Luefter (RPM = 480000 / Rohwert)
        Invoke-Read 'Get_Power' 0   # hier stecken vermutlich die Power-Limits
        Invoke-Read 'Get_Device' 0
    } catch {
        Log "  Setup fuer Testaufrufe fehlgeschlagen: $($_.Exception.Message)"
    }
    Log "  (Es wurde NICHTS gesetzt - ausschliesslich Lesezugriffe.)"
}

Log ""
Log "=== FERTIG ==="
Log "Bitte schick mir den kompletten Inhalt dieser Datei:"
Log "  $log"
Read-Host "Enter zum Beenden"
