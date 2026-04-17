# SuavoAgent Binary Upgrade — preserves appsettings.json and state.db
# Run as Administrator from any directory
# Usage: .\upgrade.ps1 [-Tag v3.9.2-p0]
param(
    [string]$Tag = "v3.9.2-p0",
    [string]$InstallDir = "C:\Program Files\Suavo\Agent"
)

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Error "Run as Administrator"; exit 1 }

$baseUrl = "https://github.com/MinaH153/SuavoAgent/releases/download/$Tag"
$binaries = @("SuavoAgent.Core.exe", "SuavoAgent.Broker.exe", "SuavoAgent.Helper.exe")
$services = @("SuavoAgent.Broker", "SuavoAgent.Core")
$tmpDir = Join-Path $env:TEMP "SuavoUpgrade_$Tag"

Write-Host "=== SuavoAgent Upgrade to $Tag ===" -ForegroundColor Cyan

# 1. Download binaries to temp
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
Write-Host "[1/4] Downloading binaries..." -ForegroundColor Yellow
foreach ($bin in $binaries) {
    $url = "$baseUrl/$bin"
    $dest = Join-Path $tmpDir $bin
    Write-Host "  $bin"
    Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
}

# 2. Stop services
Write-Host "[2/4] Stopping services..." -ForegroundColor Yellow
foreach ($svc in $services) {
    try { Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue } catch {}
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Service -Name $svc -ErrorAction SilentlyContinue).Status -ne "Stopped") {
        if ((Get-Date) -gt $deadline) { Write-Warning "$svc did not stop cleanly"; break }
        Start-Sleep -Milliseconds 200
    }
    Write-Host "  $svc stopped"
}

# 3. Replace binaries
Write-Host "[3/4] Installing new binaries..." -ForegroundColor Yellow
foreach ($bin in $binaries) {
    $src = Join-Path $tmpDir $bin
    $dst = Join-Path $InstallDir $bin
    Copy-Item -Path $src -Destination $dst -Force
    Write-Host "  $bin replaced"
}

# 4. Start services
Write-Host "[4/4] Starting services..." -ForegroundColor Yellow
foreach ($svc in ($services | Sort-Object -Descending)) {
    Start-Service -Name $svc
    Write-Host "  $svc started"
}

Remove-Item -Path $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "=== Upgrade to $Tag complete ===" -ForegroundColor Green
Write-Host "appsettings.json and state.db were NOT touched." -ForegroundColor Gray
