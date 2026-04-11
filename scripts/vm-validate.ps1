# SuavoAgent VM Validation Script
# Run on Windows VM after transferring binaries
# Usage: powershell -ExecutionPolicy Bypass -File vm-validate.ps1

$ErrorActionPreference = "Stop"
$installDir = "C:\Program Files\Suavo\Agent"
$dataDir = "$env:ProgramData\SuavoAgent"
$pass = 0
$fail = 0

function Check($name, $test) {
    try {
        $result = & $test
        if ($result) {
            Write-Host "  [PASS] $name" -ForegroundColor Green
            $script:pass++
        } else {
            Write-Host "  [FAIL] $name" -ForegroundColor Red
            $script:fail++
        }
    } catch {
        Write-Host "  [FAIL] $name — $($_.Exception.Message)" -ForegroundColor Red
        $script:fail++
    }
}

Write-Host "`n=== SuavoAgent VM Validation ===" -ForegroundColor Cyan

# 1. Service starts clean
Write-Host "`n--- Service Status ---" -ForegroundColor Yellow
Check "Core service exists" { (Get-Service SuavoAgent.Core -ErrorAction SilentlyContinue) -ne $null }
Check "Broker service exists" { (Get-Service SuavoAgent.Broker -ErrorAction SilentlyContinue) -ne $null }
Check "Core is running" { (Get-Service SuavoAgent.Core).Status -eq "Running" }
Check "Broker is running" { (Get-Service SuavoAgent.Broker).Status -eq "Running" }
Check "Broker runs as NetworkService" {
    $svc = Get-WmiObject Win32_Service -Filter "Name='SuavoAgent.Broker'"
    $svc.StartName -eq "NT AUTHORITY\NetworkService"
}

# 2. ProgramData ACL lockdown
Write-Host "`n--- ACL Lockdown ---" -ForegroundColor Yellow
Check "ProgramData dir exists" { Test-Path $dataDir }
Check "state.db exists" { Test-Path (Join-Path $dataDir "state.db") }
Check "ACL is protected (no inheritance)" {
    $acl = Get-Acl $dataDir
    $acl.AreAccessRulesProtected
}

# 3. Install dir ACL — LocalService has Modify
Write-Host "`n--- Install Dir ACL ---" -ForegroundColor Yellow
Check "Install dir exists" { Test-Path $installDir }
Check "LocalService has Modify on install dir" {
    $acl = Get-Acl $installDir
    $rules = $acl.Access | Where-Object { $_.IdentityReference -match "LOCAL SERVICE" }
    $rules | Where-Object { $_.FileSystemRights -match "Modify" }
}

# 4. Heartbeat (check logs)
Write-Host "`n--- Heartbeat ---" -ForegroundColor Yellow
$logDir = Join-Path $dataDir "logs"
Check "Log directory exists" { Test-Path $logDir }
$coreLog = Get-ChildItem "$logDir\core-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($coreLog) {
    $logContent = Get-Content $coreLog.FullName -Tail 100
    Check "Heartbeat OK in logs" { $logContent -match "Heartbeat OK" }
    Check "No HIPAA alerts" { -not ($logContent -match "HIPAA ALERT") }
    Check "Audit chain valid on startup" { -not ($logContent -match "Audit chain integrity.*FAILED") }
} else {
    Write-Host "  [SKIP] No core log found" -ForegroundColor Yellow
}

# 5. CheckPendingUpdate test — create fake sentinel
Write-Host "`n--- CheckPendingUpdate (fake sentinel) ---" -ForegroundColor Yellow
$sentinel = Join-Path $installDir "update-pending.flag"
if (-not (Test-Path $sentinel)) {
    # Write a deliberately INVALID sentinel (bad signature) — should be cleaned up
    Set-Content $sentinel "invalid|manifest`ninvalid_signature"
    Stop-Service SuavoAgent.Core -Force -ErrorAction SilentlyContinue
    Start-Sleep 2
    Start-Service SuavoAgent.Core
    Start-Sleep 5
    Check "Invalid sentinel was cleaned up" { -not (Test-Path $sentinel) }
} else {
    Write-Host "  [SKIP] Sentinel already exists — not testing" -ForegroundColor Yellow
}

# 6. IPC pipe
Write-Host "`n--- IPC ---" -ForegroundColor Yellow
Check "Named pipe SuavoAgent exists" {
    [System.IO.Directory]::GetFiles("\\.\pipe\") -match "SuavoAgent"
}

# Summary
Write-Host "`n=== Results ===" -ForegroundColor Cyan
Write-Host "  Pass: $pass  Fail: $fail" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
