# SuavoAgent — Pre-install compatibility checker
#
# USAGE (paste into ANY PowerShell, admin not required):
#   Set-ExecutionPolicy Bypass -Scope Process -Force; irm https://raw.githubusercontent.com/MinaH153/SuavoAgent/main/suavo-check.ps1 | iex
#
# What it does (in 20 seconds, nothing to install, nothing to consent to):
#   - Inventories the workstation against SuavoAgent's install requirements
#   - Emits PASS / WARN / FAIL for every check with plain-English reason
#   - Writes a detailed JSON report to the Desktop so IT can inspect/share it
#
# What it does NOT do:
#   - Read pharmacy data
#   - Send anything to the cloud
#   - Install any software, create services, or modify the registry
#   - Require elevation (admin is optional; some checks are more accurate as admin)
#
# Exit codes:
#   0 — PASS (all required checks green)
#   1 — WARN (install will likely work but something is suboptimal)
#   2 — FAIL (install will not succeed on this machine today)

# Allow running even if the rest of the session is Strict.
$ErrorActionPreference = "Continue"

$suavoCheckVersion = "1.0.0"

# ---- result collector ----------------------------------------------------
$results = New-Object System.Collections.Generic.List[object]

function Add-Result {
    param(
        [string]$Name,
        [string]$Status,   # pass|warn|fail
        [string]$Detail,
        [hashtable]$Data = $null
    )
    $results.Add([pscustomobject]@{
        name   = $Name
        status = $Status
        detail = $Detail
        data   = $Data
    })
    $color = switch ($Status) { 'pass' { 'Green' } 'warn' { 'Yellow' } 'fail' { 'Red' } default { 'Gray' } }
    $tag   = switch ($Status) { 'pass' { '[ PASS ]' } 'warn' { '[ WARN ]' } 'fail' { '[ FAIL ]' } default { '[ INFO ]' } }
    Write-Host "  $tag  $Name — $Detail" -ForegroundColor $color
}

Write-Host ""
Write-Host "  SuavoAgent pre-install check v$suavoCheckVersion" -ForegroundColor Cyan
Write-Host "  Machine: $env:COMPUTERNAME ($env:USERNAME)" -ForegroundColor DarkGray
Write-Host "  Running from: $(Get-Location)" -ForegroundColor DarkGray
Write-Host ""

# ---- 1. PowerShell version ----------------------------------------------
$psMajor = $PSVersionTable.PSVersion.Major
$psMinor = $PSVersionTable.PSVersion.Minor
$psFull  = $PSVersionTable.PSVersion.ToString()
if ($psMajor -lt 3) {
    Add-Result 'PowerShell version' 'fail' "PS $psFull — SuavoAgent requires 5.1+" @{ version = $psFull }
} elseif ($psMajor -lt 5 -or ($psMajor -eq 5 -and $psMinor -lt 1)) {
    Add-Result 'PowerShell version' 'warn' "PS $psFull — bootstrap may refuse; upgrade to 5.1" @{ version = $psFull }
} else {
    Add-Result 'PowerShell version' 'pass' "PS $psFull" @{ version = $psFull }
}

# ---- 2. .NET Framework (for Windows PowerShell 5.1 signature verify) ----
$netRelease = $null
try {
    $netRelease = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" -ErrorAction Stop).Release
} catch {}
if ($netRelease -and $netRelease -ge 528040) {
    Add-Result '.NET Framework (Windows PS)' 'pass' ".NET 4.8+ (release $netRelease)" @{ release = $netRelease }
} elseif ($netRelease -and $netRelease -ge 461808) {
    Add-Result '.NET Framework (Windows PS)' 'warn' ".NET 4.7.2 (release $netRelease) — upgrade to 4.8 recommended" @{ release = $netRelease }
} elseif ($netRelease) {
    Add-Result '.NET Framework (Windows PS)' 'warn' "Older .NET detected (release $netRelease)" @{ release = $netRelease }
} else {
    Add-Result '.NET Framework (Windows PS)' 'warn' 'Could not read .NET registry key — may be a PSCore / non-Windows host' @{ release = $null }
}

# ---- 3. Disk space on system drive --------------------------------------
$sysDriveLetter = ($env:SystemDrive).TrimEnd(':')
$freeBytes = 0
try {
    $vol = Get-CimInstance -ClassName Win32_LogicalDisk -Filter "DeviceID='$env:SystemDrive'" -ErrorAction Stop
    $freeBytes = [int64]$vol.FreeSpace
} catch {}
$freeGb = [math]::Round($freeBytes / 1GB, 2)
if ($freeBytes -lt 500MB) {
    Add-Result 'Disk space (SystemDrive)' 'fail' "$freeGb GB free — need at least 500 MB" @{ free_gb = $freeGb; drive = $env:SystemDrive }
} elseif ($freeBytes -lt 2GB) {
    Add-Result 'Disk space (SystemDrive)' 'warn' "$freeGb GB free — 2 GB recommended" @{ free_gb = $freeGb; drive = $env:SystemDrive }
} else {
    Add-Result 'Disk space (SystemDrive)' 'pass' "$freeGb GB free on $env:SystemDrive" @{ free_gb = $freeGb; drive = $env:SystemDrive }
}

# ---- 4. BitLocker status ------------------------------------------------
$bitStatus = $null
try {
    $bitStatus = (manage-bde -status $env:SystemDrive 2>$null | Select-String "Protection Status" | Select-Object -First 1).ToString().Trim()
} catch {}
if ($bitStatus -match 'Protection On') {
    Add-Result 'BitLocker (HIPAA safe harbor)' 'pass' 'Protection On' @{ protection = 'on' }
} elseif ($bitStatus) {
    Add-Result 'BitLocker (HIPAA safe harbor)' 'warn' $bitStatus @{ protection = 'off' }
} else {
    Add-Result 'BitLocker (HIPAA safe harbor)' 'warn' 'Unable to query manage-bde — may need admin' @{ protection = 'unknown' }
}

# ---- 5. PioneerRx presence ----------------------------------------------
$pioneerExe = $null
$pioneerDir = $null
$pioneerPaths = @(
    "C:\Program Files (x86)\New Tech Computer Systems\PioneerRx",
    "C:\Program Files\New Tech Computer Systems\PioneerRx",
    "D:\Program Files (x86)\New Tech Computer Systems\PioneerRx",
    "D:\Program Files\New Tech Computer Systems\PioneerRx"
)
try {
    foreach ($rp in @(
        'HKLM:\SOFTWARE\WOW6432Node\New Tech Computer Systems',
        'HKLM:\SOFTWARE\New Tech Computer Systems'
    )) {
        if (Test-Path $rp) {
            $regInstall = (Get-ItemProperty $rp -ErrorAction SilentlyContinue).InstallPath
            if ($regInstall) { $pioneerPaths = @($regInstall) + $pioneerPaths }
        }
    }
} catch {}
foreach ($p in $pioneerPaths) {
    $exe = [System.IO.Path]::Combine($p, 'PioneerPharmacy.exe')
    if (Test-Path $exe) { $pioneerDir = $p; $pioneerExe = $exe; break }
}
if (-not $pioneerExe) {
    $proc = Get-Process -Name 'PioneerPharmacy' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc) { $pioneerExe = $proc.Path; $pioneerDir = Split-Path $pioneerExe -Parent }
}
if ($pioneerExe) {
    Add-Result 'PioneerRx installation' 'pass' "Detected at $pioneerDir" @{ exe = $pioneerExe; dir = $pioneerDir }
} else {
    Add-Result 'PioneerRx installation' 'warn' 'No PioneerRx on this machine — agent will still install and auto-discover during learning phase' @{ exe = $null; dir = $null }
}

# ---- 6. SQL Server reachability ----------------------------------------
$sqlHost = $null
$sqlReachable = $false
try {
    # Hunt the PioneerPharmacy.exe.config for its connection string, if present.
    $cfg = if ($pioneerExe) { "$pioneerExe.config" } else { $null }
    if ($cfg -and (Test-Path $cfg)) {
        $xml = [xml](Get-Content $cfg -Raw)
        $cs = $xml.SelectSingleNode("//connectionStrings/add[@name='ConnectionStringServer']")
        if ($cs -and $cs.connectionString) {
            $match = [regex]::Match($cs.connectionString, 'Data Source=([^;]+)')
            if ($match.Success) { $sqlHost = $match.Groups[1].Value }
        }
    }
} catch {}
if ($sqlHost) {
    $hostOnly = ($sqlHost -split '[\\,]')[0]
    try {
        $test = Test-NetConnection -ComputerName $hostOnly -Port 1433 -WarningAction SilentlyContinue -InformationLevel Quiet 2>$null
        $sqlReachable = [bool]$test
    } catch {}
    if ($sqlReachable) {
        Add-Result 'SQL Server reachability' 'pass' "Reachable: $sqlHost (port 1433)" @{ host = $sqlHost; port_open = $true }
    } else {
        Add-Result 'SQL Server reachability' 'warn' "Could not reach $sqlHost on 1433 — may use dynamic port or named pipe" @{ host = $sqlHost; port_open = $false }
    }
} else {
    Add-Result 'SQL Server reachability' 'warn' 'No ConnectionStringServer found — agent will auto-discover during install' @{ host = $null; port_open = $null }
}

# ---- 7. Outbound HTTPS to suavollc.com ---------------------------------
$cloudReachable = $false
try {
    $cloudTest = Test-NetConnection -ComputerName 'suavollc.com' -Port 443 -WarningAction SilentlyContinue -InformationLevel Quiet 2>$null
    $cloudReachable = [bool]$cloudTest
} catch {}
if ($cloudReachable) {
    Add-Result 'Outbound HTTPS (suavollc.com:443)' 'pass' 'Reachable' @{ reachable = $true }
} else {
    Add-Result 'Outbound HTTPS (suavollc.com:443)' 'fail' 'Cannot reach suavollc.com on 443 — check firewall / proxy' @{ reachable = $false }
}

# ---- 8. Antivirus inventory (log only; never fail) ---------------------
$av = @()
try {
    $av = Get-CimInstance -Namespace 'root\SecurityCenter2' -ClassName AntiVirusProduct -ErrorAction Stop |
        Select-Object -ExpandProperty displayName
} catch {}
if ($av.Count -gt 0) {
    Add-Result 'Antivirus inventory' 'pass' "Detected: $($av -join ', ')" @{ products = $av }
} else {
    Add-Result 'Antivirus inventory' 'warn' 'No AV products surfaced via SecurityCenter2 (pharmacy workstations should run one)' @{ products = @() }
}

# ---- 9. Windows Defender exclusion check --------------------------------
$exclusionPath = "C:\Program Files\Suavo\Agent"
$hasExclusion = $false
try {
    $prefs = Get-MpPreference -ErrorAction Stop
    if ($prefs.ExclusionPath -and ($prefs.ExclusionPath -contains $exclusionPath)) {
        $hasExclusion = $true
    }
} catch {}
if ($hasExclusion) {
    Add-Result 'Defender exclusion' 'pass' "$exclusionPath already excluded" @{ path = $exclusionPath; excluded = $true }
} else {
    Add-Result 'Defender exclusion' 'warn' "Recommended: add $exclusionPath to Defender exclusions to avoid scan-induced restart flapping" @{ path = $exclusionPath; excluded = $false }
}

# ---- 10. Admin privilege (informational) -------------------------------
$isAdmin = $false
try {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
} catch {}
if ($isAdmin) {
    Add-Result 'Running as administrator' 'pass' 'Installer will run without elevation prompt' @{ admin = $true }
} else {
    Add-Result 'Running as administrator' 'warn' 'Install will require elevation — right-click PowerShell > Run as Administrator' @{ admin = $false }
}

# ---- summary -----------------------------------------------------------
$failCount = ($results | Where-Object { $_.status -eq 'fail' }).Count
$warnCount = ($results | Where-Object { $_.status -eq 'warn' }).Count
$passCount = ($results | Where-Object { $_.status -eq 'pass' }).Count

$verdict = if ($failCount -gt 0) { 'FAIL' } elseif ($warnCount -gt 0) { 'WARN' } else { 'PASS' }
$color   = if ($verdict -eq 'FAIL') { 'Red' } elseif ($verdict -eq 'WARN') { 'Yellow' } else { 'Green' }

Write-Host ""
Write-Host "  Verdict: $verdict — $passCount pass / $warnCount warn / $failCount fail" -ForegroundColor $color
Write-Host ""

# ---- write detailed JSON for IT handoff --------------------------------
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$desktopPath = [Environment]::GetFolderPath('Desktop')
$reportPath  = Join-Path $desktopPath "suavo-precheck-$timestamp.json"

$report = @{
    suavo_check_version = $suavoCheckVersion
    generated_at        = (Get-Date).ToString('o')
    machine             = $env:COMPUTERNAME
    user                = $env:USERNAME
    verdict             = $verdict
    totals              = @{ pass = $passCount; warn = $warnCount; fail = $failCount }
    results             = $results
}
try {
    $report | ConvertTo-Json -Depth 6 | Set-Content -Path $reportPath -Encoding UTF8
    Write-Host "  Detailed report: $reportPath" -ForegroundColor Cyan
} catch {
    Write-Host "  Could not write report: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host "  Share that JSON file with whoever is onboarding you — it has everything we need to prep your install." -ForegroundColor DarkGray
Write-Host ""

# Exit code reflects verdict so scripted callers can gate on it.
if ($verdict -eq 'FAIL') { exit 2 }
elseif ($verdict -eq 'WARN') { exit 1 }
else { exit 0 }
