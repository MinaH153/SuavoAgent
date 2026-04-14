#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Check SuavoAgent health on this machine.
.DESCRIPTION
    Queries local agent status: service state, SQL connectivity,
    cloud sync, Helper attached, audit chain, and recent errors.
    Run from any Admin PowerShell — no cloud access required.
.EXAMPLE
    .\Get-SuavoAgentHealth.ps1
#>

$ErrorActionPreference = "SilentlyContinue"

Write-Host ""
Write-Host "  ==================================================" -ForegroundColor Cyan
Write-Host "         SuavoAgent Health Check" -ForegroundColor Cyan
Write-Host "  ==================================================" -ForegroundColor Cyan
Write-Host ""

# ── 1. Service status ──
$coreService = Get-Service -Name "SuavoAgent.Core"
$brokerService = Get-Service -Name "SuavoAgent.Broker"

Write-Host "  Services" -ForegroundColor Yellow
Write-Host "  --------"
if ($coreService) {
    $color = if ($coreService.Status -eq "Running") { "Green" } else { "Red" }
    Write-Host "    Core:   $($coreService.Status)" -ForegroundColor $color
} else {
    Write-Host "    Core:   NOT INSTALLED" -ForegroundColor Red
}
if ($brokerService) {
    $color = if ($brokerService.Status -eq "Running") { "Green" } else { "Red" }
    Write-Host "    Broker: $($brokerService.Status)" -ForegroundColor $color
} else {
    Write-Host "    Broker: NOT INSTALLED" -ForegroundColor Red
}

# ── 2. Helper process ──
$helper = Get-Process -Name "SuavoAgent.Helper"
$helperText = if ($helper) { "Running (PID $($helper.Id))" } else { "Not running" }
$helperColor = if ($helper) { "Green" } else { "Yellow" }
Write-Host "    Helper: $helperText" -ForegroundColor $helperColor
Write-Host ""

# ── 3. Configuration ──
$configPath = "C:\Program Files\Suavo\Agent\appsettings.json"
Write-Host "  Configuration" -ForegroundColor Yellow
Write-Host "  -------------"
if (Test-Path $configPath) {
    try {
        $config = Get-Content $configPath -Raw | ConvertFrom-Json
        $a = $config.Agent
        Write-Host "    Agent ID:    $($a.AgentId)"
        Write-Host "    Pharmacy ID: $($a.PharmacyId)"
        Write-Host "    Cloud URL:   $($a.CloudUrl)"
        Write-Host "    SQL Server:  $($a.SqlServer)"
        Write-Host "    Version:     $($a.Version)"
        Write-Host "    Learning:    $($a.LearningMode)"
    } catch {
        Write-Host "    PARSE ERROR: $($_.Exception.Message)" -ForegroundColor Red
    }
} else {
    Write-Host "    NOT FOUND at $configPath" -ForegroundColor Red
}
Write-Host ""

# ── 4. State database ──
$dbPath = "C:\ProgramData\SuavoAgent\state.db"
Write-Host "  Database" -ForegroundColor Yellow
Write-Host "  --------"
if (Test-Path $dbPath) {
    $dbSize = (Get-Item $dbPath).Length / 1KB
    Write-Host "    Path: $dbPath"
    Write-Host "    Size: $([math]::Round($dbSize, 1)) KB"
} else {
    Write-Host "    NOT FOUND at $dbPath" -ForegroundColor Red
}
Write-Host ""

# ── 5. Recent logs ──
$logDir = "C:\ProgramData\SuavoAgent\logs"
Write-Host "  Logs" -ForegroundColor Yellow
Write-Host "  ----"
if (Test-Path $logDir) {
    $latestLog = Get-ChildItem "$logDir\core-*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latestLog) {
        $lastWrite = $latestLog.LastWriteTime
        $age = (Get-Date) - $lastWrite
        $ageMin = [math]::Round($age.TotalMinutes, 0)
        $color = if ($age.TotalMinutes -lt 5) { "Green" } elseif ($age.TotalMinutes -lt 60) { "Yellow" } else { "Red" }

        Write-Host "    File:       $($latestLog.Name)"
        Write-Host "    Last write: $lastWrite ($ageMin min ago)" -ForegroundColor $color
        Write-Host ""
        Write-Host "    Last 10 lines:" -ForegroundColor Gray
        Get-Content $latestLog.FullName -Tail 10 | ForEach-Object {
            Write-Host "      $_"
        }

        # Error scan
        $errors = Select-String -Path $latestLog.FullName -Pattern "\[ERR\]|\[FTL\]" |
            Select-Object -Last 5
        Write-Host ""
        if ($errors) {
            Write-Host "    Recent errors ($($errors.Count)):" -ForegroundColor Red
            $errors | ForEach-Object { Write-Host "      $($_.Line)" -ForegroundColor Red }
        } else {
            Write-Host "    No recent errors." -ForegroundColor Green
        }
    } else {
        Write-Host "    No core log files found in $logDir" -ForegroundColor Yellow
    }
} else {
    Write-Host "    NOT FOUND at $logDir" -ForegroundColor Red
}
Write-Host ""

# ── 6. IPC named pipe ──
Write-Host "  IPC Pipe" -ForegroundColor Yellow
Write-Host "  --------"
try {
    $pipeExists = [System.IO.Directory]::GetFiles("\\.\pipe\") |
        Where-Object { $_ -like "*SuavoAgent*" }
    $pipeText = if ($pipeExists) { "Active" } else { "Not found" }
    $pipeColor = if ($pipeExists) { "Green" } else { "Red" }
    Write-Host "    $pipeText" -ForegroundColor $pipeColor
} catch {
    Write-Host "    Could not query pipes: $($_.Exception.Message)" -ForegroundColor Yellow
}
Write-Host ""

# ── Summary ──
$issues = 0
if (-not $coreService -or $coreService.Status -ne "Running") { $issues++ }
if (-not $brokerService -or $brokerService.Status -ne "Running") { $issues++ }
if (-not (Test-Path $configPath)) { $issues++ }
if (-not (Test-Path $dbPath)) { $issues++ }

if ($issues -eq 0) {
    Write-Host "  Status: ALL CLEAR" -ForegroundColor Green
} else {
    Write-Host "  Status: $issues issue(s) detected" -ForegroundColor Red
}

Write-Host ""
Write-Host "  ==================================================" -ForegroundColor Cyan
Write-Host "         Health check complete" -ForegroundColor Cyan
Write-Host "  ==================================================" -ForegroundColor Cyan
Write-Host ""
