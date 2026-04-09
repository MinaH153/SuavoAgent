# SuavoAgent v2 — Windows Development Setup
# Run this in PowerShell (Admin) on the Windows 11 VM
# The Mac shared folder should be accessible at \\Mac\Home\Documents\SuavoAgent

$ErrorActionPreference = "Stop"
Write-Host "=== SuavoAgent Windows Setup ===" -ForegroundColor Cyan

# Step 1: Install .NET 8 SDK if not present
$dotnetVersion = dotnet --version 2>$null
if ($dotnetVersion -like "8.*") {
    Write-Host "[OK] .NET SDK $dotnetVersion already installed" -ForegroundColor Green
} else {
    Write-Host "[INSTALL] Downloading .NET 8 SDK..." -ForegroundColor Yellow
    $installerUrl = "https://dot.net/v1/dotnet-install.ps1"
    $installScript = "$env:TEMP\dotnet-install.ps1"
    Invoke-WebRequest -Uri $installerUrl -OutFile $installScript
    & $installScript -Channel 8.0 -InstallDir "$env:ProgramFiles\dotnet"
    $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
    Write-Host "[OK] .NET 8 SDK installed: $(dotnet --version)" -ForegroundColor Green
}

# Step 2: Copy project from Mac shared folder to local Windows drive
$macPath = "\\Mac\Home\Documents\SuavoAgent"
$winPath = "C:\SuavoAgent"

if (Test-Path $macPath) {
    Write-Host "[COPY] Copying from Mac shared folder to $winPath..." -ForegroundColor Yellow
    if (Test-Path $winPath) { Remove-Item -Recurse -Force $winPath }
    Copy-Item -Recurse -Path $macPath -Destination $winPath -Exclude @("bin", "obj", ".git")
    Write-Host "[OK] Copied to $winPath" -ForegroundColor Green
} else {
    Write-Host "[WARN] Mac shared folder not found at $macPath" -ForegroundColor Red
    Write-Host "  Make sure Parallels shared folders are enabled" -ForegroundColor Red
    Write-Host "  Or manually copy ~/Documents/SuavoAgent to C:\SuavoAgent" -ForegroundColor Red
    exit 1
}

# Step 3: Build and test
Write-Host "`n[BUILD] Building solution..." -ForegroundColor Yellow
Set-Location $winPath
dotnet restore
dotnet build --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FAIL] Build failed" -ForegroundColor Red
    exit 1
}
Write-Host "[OK] Build succeeded" -ForegroundColor Green

Write-Host "`n[TEST] Running tests..." -ForegroundColor Yellow
dotnet test --no-build -v minimal
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FAIL] Tests failed" -ForegroundColor Red
    exit 1
}
Write-Host "[OK] All tests passed" -ForegroundColor Green

# Step 4: Verify Windows-specific capabilities
Write-Host "`n[CHECK] Verifying Windows service capabilities..." -ForegroundColor Yellow
$scExists = Get-Command sc.exe -ErrorAction SilentlyContinue
if ($scExists) { Write-Host "  sc.exe: Available" -ForegroundColor Green }
$regTask = Get-Command Register-ScheduledTask -ErrorAction SilentlyContinue
if ($regTask) { Write-Host "  Register-ScheduledTask: Available" -ForegroundColor Green }

Write-Host "`n=== Setup Complete ===" -ForegroundColor Cyan
Write-Host "Project at: $winPath"
Write-Host "Next: Run 'dotnet run --project src\SuavoAgent.Core' to test the service locally"
