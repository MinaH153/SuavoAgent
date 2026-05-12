# SuavoAgent v2 -Windows Service Installer
# Run as Administrator
param(
    [string]$InstallDir = "C:\Program Files\Suavo\Agent",
    [string]$PublishDir = ".\publish",
    [switch]$Uninstall,
    [switch]$KeepData,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# Require admin
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: Run as Administrator" -ForegroundColor Red
    exit 1
}

$serviceBroker = "SuavoAgent.Broker"
$serviceCore = "SuavoAgent.Core"
$serviceWatchdog = "SuavoAgent.Watchdog"
$services = @($serviceWatchdog, $serviceBroker, $serviceCore)
$dataDir = "$env:ProgramData\SuavoAgent"

function Wait-ServiceRunning {
    param([Parameter(Mandatory)][string]$Name, [int]$TimeoutSec = 45)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -eq 'Running') { return $true }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return $false
}

function Archive-CrashLogs {
    $logDir = Join-Path $dataDir "logs"
    if (-not (Test-Path $logDir)) { return }
    $stamp = Get-Date -Format "yyyyMMddHHmmss"
    foreach ($name in @("startup-crash.log", "broker-crash.log", "watchdog-crash.log")) {
        $path = Join-Path $logDir $name
        if ((Test-Path $path) -and ((Get-Item $path).Length -gt 0)) {
            Copy-Item $path (Join-Path $logDir "$($name).preinstall-$stamp") -Force
            Clear-Content $path -ErrorAction SilentlyContinue
        }
    }
}

function Run-SuavoAgentSmokeStrict {
    $logDir = Join-Path $dataDir "logs"

    foreach ($svc in @($serviceCore, $serviceBroker, $serviceWatchdog)) {
        $s = Get-Service -Name $svc -ErrorAction Stop
        if ($s.Status -ne 'Running') {
            throw "$svc failed strict smoke: status is $($s.Status)"
        }
    }

    foreach ($proc in @("SuavoAgent.Core", "SuavoAgent.Broker", "SuavoAgent.Watchdog")) {
        if (-not (Get-Process -Name $proc -ErrorAction SilentlyContinue)) {
            throw "$proc failed strict smoke: process missing"
        }
    }

    $configPath = Join-Path $InstallDir "appsettings.json"
    if (Test-Path $configPath) {
        $config = Get-Content $configPath -Raw | ConvertFrom-Json
        if ($config.Agent.Vision.Enabled -ne $false -or $config.Agent.AutoExecution.Enabled -ne $false) {
            throw "Unsafe smoke config: Vision.Enabled=$($config.Agent.Vision.Enabled), AutoExecution.Enabled=$($config.Agent.AutoExecution.Enabled)"
        }
    }

    foreach ($name in @("startup-crash.log", "broker-crash.log", "watchdog-crash.log")) {
        $path = Join-Path $logDir $name
        if ((Test-Path $path) -and ((Get-Item $path).Length -gt 0)) {
            Write-Host "`n--- $name ---" -ForegroundColor Red
            Get-Content $path -Tail 20
            throw "$name contains crash output"
        }
    }
}

function Invoke-SecureDeleteFile {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    try {
        $fi = Get-Item -LiteralPath $Path -Force
        $len = [int64]$fi.Length
        if ($len -gt 0) {
            $buffer = New-Object byte[] ([Math]::Min($len, 1048576))
            $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
            $fs = [System.IO.File]::Open($Path, 'Open', 'Write', 'None')
            try {
                $written = [int64]0
                while ($written -lt $len) {
                    $chunk = [Math]::Min($buffer.Length, $len - $written)
                    $rng.GetBytes($buffer, 0, $chunk)
                    $fs.Write($buffer, 0, $chunk)
                    $written += $chunk
                }
                $fs.Flush($true)
            } finally {
                $fs.Dispose()
                $rng.Dispose()
            }
        }
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
    } catch {
        Write-Host "  [warn] secure-delete failed for $Path - $_" -ForegroundColor Yellow
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-SecureWipeDir {
    param([Parameter(Mandatory)][string]$Root)
    if (-not (Test-Path -LiteralPath $Root)) { return }
    $sensitive = @('*.dat', 'state.db', 'state.db-wal', 'state.db-shm', 'state.key',
        'consent-receipt.json', 'audit-*.json', '*.db', '*.key')
    foreach ($pattern in $sensitive) {
        Get-ChildItem -LiteralPath $Root -Recurse -Force -File -Filter $pattern -ErrorAction SilentlyContinue |
            ForEach-Object { Invoke-SecureDeleteFile -Path $_.FullName }
    }
    Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($Uninstall) {
    Write-Host "=== Uninstalling SuavoAgent ===" -ForegroundColor Yellow

    if (-not $KeepData -and -not $Force) {
        Write-Host ""
        Write-Host "This will SECURELY ERASE all local pharmacy data:" -ForegroundColor Red
        Write-Host "  - $dataDir\delivery-receipts\  (DEA audit receipts)" -ForegroundColor Red
        Write-Host "  - $dataDir\state.db            (encrypted agent state)" -ForegroundColor Red
        Write-Host "  - $dataDir\state.key           (DPAPI-wrapped key)" -ForegroundColor Red
        Write-Host "  - $dataDir\consent-receipt.json (HIPAA consent record)" -ForegroundColor Red
        Write-Host ""
        Write-Host "Ensure remote decommission has acknowledged the archive digest BEFORE proceeding." -ForegroundColor Yellow
        $confirm = Read-Host "Type WIPE to confirm, or KEEP to preserve data"
        if ($confirm -eq "KEEP") { $KeepData = $true }
        elseif ($confirm -ne "WIPE") {
            Write-Host "Aborted." -ForegroundColor Yellow
            exit 1
        }
    }

    # Stop services
    foreach ($svc in $services) {
        $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
        if ($s) {
            if ($s.Status -eq "Running") {
                Write-Host "Stopping $svc..."
                Stop-Service $svc -Force
                Start-Sleep -Seconds 2
            }
            Write-Host "Removing $svc..."
            sc.exe delete $svc | Out-Null
        }
    }

    Write-Host "Removing files from $InstallDir..."
    if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }

    if ($KeepData) {
        Write-Host "Data dir preserved at $dataDir (KeepData)." -ForegroundColor Yellow
    } else {
        Write-Host "Secure-wiping $dataDir..." -ForegroundColor Yellow
        Invoke-SecureWipeDir -Root $dataDir
        if (Test-Path $dataDir) {
            Write-Host "  [warn] residual files remain in $dataDir - inspect manually" -ForegroundColor Yellow
        } else {
            Write-Host "  data directory erased" -ForegroundColor Green
        }
    }

    Write-Host "=== Uninstall Complete ===" -ForegroundColor Green
    exit 0
}

Write-Host "=== Installing SuavoAgent v2 ===" -ForegroundColor Cyan
Write-Host "Install dir: $InstallDir"

# Verify publish output exists
foreach ($sub in @("Core", "Broker", "Helper", "Watchdog", "Setup")) {
    $exeName = if ($sub -eq "Setup") { "SuavoSetup.exe" } else { "SuavoAgent.$sub.exe" }
    $exe = Join-Path $PublishDir "$sub\$exeName"
    if (-not (Test-Path $exe)) {
        Write-Host "ERROR: $exe not found. Run publish.ps1 first." -ForegroundColor Red
        exit 1
    }
}

# Stop existing services
foreach ($svc in $services) {
    $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
    if ($s -and $s.Status -eq "Running") {
        Write-Host "Stopping existing $svc..."
        Stop-Service $svc -Force
        Start-Sleep -Seconds 2
    }
    if ($s) {
        sc.exe delete $svc | Out-Null
        Start-Sleep -Seconds 1
    }
}

# Create directories
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
New-Item -ItemType Directory -Path "$dataDir\logs" -Force | Out-Null

# Copy binaries
Write-Host "Copying binaries..." -ForegroundColor Yellow
foreach ($sub in @("Core", "Broker", "Helper", "Watchdog", "Setup")) {
    $exeName = if ($sub -eq "Setup") { "SuavoSetup.exe" } else { "SuavoAgent.$sub.exe" }
    $src = Join-Path $PublishDir "$sub\$exeName"
    $dst = Join-Path $InstallDir $exeName
    Copy-Item $src $dst -Force
    Write-Host "  $exeName -> $InstallDir" -ForegroundColor Gray
}

# Copy appsettings if exists
$appSettings = Join-Path $PublishDir "Core\appsettings.json"
if (Test-Path $appSettings) {
    Copy-Item $appSettings (Join-Path $InstallDir "appsettings.json") -Force
}

# H-8: Write binary integrity manifest so Broker can verify Helper before launch
Write-Host "Computing binary hashes..." -ForegroundColor Yellow
$manifest = @{}
foreach ($sub in @("Core", "Broker", "Helper", "Watchdog")) {
    $exePath = Join-Path $InstallDir "SuavoAgent.$sub.exe"
    $hash = (Get-FileHash $exePath -Algorithm SHA256).Hash.ToLower()
    $manifest["SuavoAgent.$sub.exe"] = $hash
    Write-Host "  SuavoAgent.$sub.exe: $hash" -ForegroundColor Gray
}
$manifestPath = Join-Path $dataDir "binaries.manifest"
$manifest | ConvertTo-Json | Set-Content $manifestPath -Encoding UTF8
Write-Host "  binaries.manifest written to $manifestPath" -ForegroundColor Gray

# Register Core service (least-privilege virtual account)
$corePath = Join-Path $InstallDir "SuavoAgent.Core.exe"
Write-Host "Registering $serviceCore service..." -ForegroundColor Yellow
sc.exe create $serviceCore binPath= "`"$corePath`"" start= delayed-auto obj= "NT AUTHORITY\LocalService"
sc.exe description $serviceCore "Suavo pharmacy agent - core service (SQL, cloud sync, task queue)"
sc.exe failure $serviceCore reset= 3600 actions= restart/5000/restart/30000/restart/60000
sc.exe failureflag $serviceCore 1

# Register Broker service.
#
# LocalSystem is REQUIRED for this Broker -- not a privilege escalation.
# The Broker's primary job is the cross-session launch dance:
#   WTSQueryUserToken(sessionId)
#     -> DuplicateTokenEx(SecurityImpersonation, TokenPrimary)
#       -> CreateEnvironmentBlock
#         -> CreateProcessAsUser(... SuavoAgent.Helper.exe ...)
# `WTSQueryUserToken` is documented as requiring the SE_TCB_NAME privilege
# ("Caller must be running in the LocalSystem account and must have the
# SE_TCB_NAME privilege"). NetworkService does NOT hold SeTcbPrivilege --
# with that identity the call fails Win32(1314) ERROR_PRIVILEGE_NOT_HELD,
# Broker silently falls back to the dev-only `Process.Start` path, and
# Helper ends up in Session 0 as NETWORK SERVICE. From Session 0 Helper
# cannot drive the active user's desktop, so SendInput / UIA / hWnd
# interaction all return Win32(5) ACCESS_DENIED.
#
# That was the second root cause behind Bug 22, surfaced by Queen
# verification after PR #65 only fixed the token impersonation level.
# See bug-22-session-0-isolation-helper memory entry + Codex review of
# #65 ("completeness conditional on Queen actually using the native
# path vs the SessionWatcher fallback").
$brokerPath = Join-Path $InstallDir "SuavoAgent.Broker.exe"
Write-Host "Registering $serviceBroker service..." -ForegroundColor Yellow
sc.exe create $serviceBroker binPath= "`"$brokerPath`"" start= delayed-auto obj= LocalSystem
sc.exe description $serviceBroker "Suavo pharmacy agent - session broker (launches UI helper)"
sc.exe failure $serviceBroker reset= 3600 actions= restart/5000/restart/30000/restart/60000
sc.exe failureflag $serviceBroker 1

# Set Broker to depend on Core
sc.exe config $serviceBroker depend= $serviceCore

# Register Watchdog service (LocalSystem so it can repair Core/Broker)
$watchdogPath = Join-Path $InstallDir "SuavoAgent.Watchdog.exe"
Write-Host "Registering $serviceWatchdog service..." -ForegroundColor Yellow
sc.exe create $serviceWatchdog binPath= "`"$watchdogPath`"" start= delayed-auto obj= LocalSystem
sc.exe description $serviceWatchdog "Suavo pharmacy agent - process watchdog (auto-restarts Core/Broker, escalates to bootstrap --repair)"
sc.exe failure $serviceWatchdog reset= 3600 actions= restart/10000/restart/60000/restart/300000
sc.exe failureflag $serviceWatchdog 1

# Lock down install directory ACL
Write-Host "Setting directory permissions..." -ForegroundColor Yellow
$acl = Get-Acl $InstallDir
$acl.SetAccessRuleProtection($true, $false)
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("BUILTIN\Administrators", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("NT AUTHORITY\SYSTEM", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("NT AUTHORITY\LOCAL SERVICE", "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")))
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("NT AUTHORITY\NETWORK SERVICE", "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")))
Set-Acl $InstallDir $acl

# Lock down data directory
$aclData = Get-Acl $dataDir
$aclData.SetAccessRuleProtection($true, $false)
$aclData.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("BUILTIN\Administrators", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
$aclData.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("NT AUTHORITY\SYSTEM", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
$aclData.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("NT AUTHORITY\LOCAL SERVICE", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")))
$aclData.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("NT AUTHORITY\NETWORK SERVICE", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")))
Set-Acl $dataDir $aclData

Archive-CrashLogs

# Start services
Write-Host "`nStarting services..." -ForegroundColor Yellow
Start-Service $serviceCore
foreach ($svc in @($serviceCore)) {
    if (-not (Wait-ServiceRunning -Name $svc -TimeoutSec 45)) { throw "$svc failed to reach Running within 45s" }
}
Start-Service $serviceBroker
foreach ($svc in @($serviceBroker)) {
    if (-not (Wait-ServiceRunning -Name $svc -TimeoutSec 45)) { throw "$svc failed to reach Running within 45s" }
}
Start-Service $serviceWatchdog
foreach ($svc in @($serviceWatchdog)) {
    if (-not (Wait-ServiceRunning -Name $svc -TimeoutSec 45)) { throw "$svc failed to reach Running within 45s" }
}

Run-SuavoAgentSmokeStrict

# Verify
Write-Host "`n=== Installation Complete ===" -ForegroundColor Cyan
foreach ($svc in @($serviceCore, $serviceBroker, $serviceWatchdog)) {
    $s = Get-Service -Name $svc
    $status = if ($s.Status -eq "Running") { "Running" } else { $s.Status }
    $color = if ($s.Status -eq "Running") { "Green" } else { "Red" }
    Write-Host "  $svc`: $status" -ForegroundColor $color
}
Write-Host "`nInstall dir: $InstallDir"
Write-Host "Data dir:    $dataDir"
Write-Host "Logs:        $dataDir\logs\"
Get-ChildItem $InstallDir -Filter "*.exe" | ForEach-Object {
    $sizeMb = [math]::Round($_.Length / 1MB, 1)
    Write-Host "  $($_.Name) -$sizeMb MB"
}
