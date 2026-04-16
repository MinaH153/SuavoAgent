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
$dataDir = "$env:ProgramData\SuavoAgent"

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
        Write-Host "  [warn] secure-delete failed for $Path — $_" -ForegroundColor Yellow
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
    foreach ($svc in @($serviceBroker, $serviceCore)) {
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
            Write-Host "  [warn] residual files remain in $dataDir — inspect manually" -ForegroundColor Yellow
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
foreach ($sub in @("Core", "Broker", "Helper")) {
    $exe = Join-Path $PublishDir "$sub\SuavoAgent.$sub.exe"
    if (-not (Test-Path $exe)) {
        Write-Host "ERROR: $exe not found. Run publish.ps1 first." -ForegroundColor Red
        exit 1
    }
}

# Stop existing services
foreach ($svc in @($serviceBroker, $serviceCore)) {
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
foreach ($sub in @("Core", "Broker", "Helper")) {
    $src = Join-Path $PublishDir "$sub\SuavoAgent.$sub.exe"
    $dst = Join-Path $InstallDir "SuavoAgent.$sub.exe"
    Copy-Item $src $dst -Force
    Write-Host "  SuavoAgent.$sub.exe -> $InstallDir" -ForegroundColor Gray
}

# Copy appsettings if exists
$appSettings = Join-Path $PublishDir "Core\appsettings.json"
if (Test-Path $appSettings) {
    Copy-Item $appSettings (Join-Path $InstallDir "appsettings.json") -Force
}

# Register Core service (least-privilege virtual account)
$corePath = Join-Path $InstallDir "SuavoAgent.Core.exe"
Write-Host "Registering $serviceCore service..." -ForegroundColor Yellow
sc.exe create $serviceCore binPath= "`"$corePath`"" start= delayed-auto obj= "NT AUTHORITY\LocalService"
sc.exe description $serviceCore "Suavo pharmacy agent - core service (SQL, cloud sync, task queue)"
sc.exe failure $serviceCore reset= 3600 actions= restart/5000/restart/30000/restart/60000
sc.exe failureflag $serviceCore 1

# Register Broker service (NetworkService for session detection)
$brokerPath = Join-Path $InstallDir "SuavoAgent.Broker.exe"
Write-Host "Registering $serviceBroker service..." -ForegroundColor Yellow
sc.exe create $serviceBroker binPath= "`"$brokerPath`"" start= delayed-auto obj= "NT AUTHORITY\NetworkService"
sc.exe description $serviceBroker "Suavo pharmacy agent - session broker (launches UI helper)"
sc.exe failure $serviceBroker reset= 3600 actions= restart/5000/restart/30000/restart/60000
sc.exe failureflag $serviceBroker 1

# Set Broker to depend on Core
sc.exe config $serviceBroker depend= $serviceCore

# Lock down install directory ACL
Write-Host "Setting directory permissions..." -ForegroundColor Yellow
$acl = Get-Acl $InstallDir
$acl.SetAccessRuleProtection($true, $false)
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("BUILTIN\Administrators", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("NT AUTHORITY\SYSTEM", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("NT AUTHORITY\LOCAL SERVICE", "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")))
Set-Acl $InstallDir $acl

# Lock down data directory
$aclData = Get-Acl $dataDir
$aclData.SetAccessRuleProtection($true, $false)
$aclData.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("BUILTIN\Administrators", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
$aclData.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("NT AUTHORITY\SYSTEM", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
$aclData.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("NT AUTHORITY\LOCAL SERVICE", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
Set-Acl $dataDir $aclData

# Start services
Write-Host "`nStarting services..." -ForegroundColor Yellow
Start-Service $serviceCore
Start-Sleep -Seconds 2
Start-Service $serviceBroker

# Verify
Write-Host "`n=== Installation Complete ===" -ForegroundColor Cyan
foreach ($svc in @($serviceCore, $serviceBroker)) {
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
