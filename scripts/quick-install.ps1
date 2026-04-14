# SuavoAgent Quick Install - downloads, configures, starts. Works on any Windows machine.
# Run from cmd.exe:  curl -L -o C:\st\qi.ps1 https://raw.githubusercontent.com/MinaH153/SuavoAgent/main/scripts/quick-install.ps1 && powershell -EP Bypass -File C:\st\qi.ps1

$dir = "C:\Program Files\Suavo\Agent"
$data = "$env:ProgramData\SuavoAgent"
$tag = "v3.0.2"
$base = "https://github.com/MinaH153/SuavoAgent/releases/download/$tag"

# --- Phase 0: Kill everything from previous installs ---
Write-Host "Cleaning up existing install..." -ForegroundColor Yellow
foreach ($s in @("SuavoAgent.Broker","SuavoAgent.Core")) {
    Stop-Service $s -Force -ErrorAction SilentlyContinue
}
Start-Sleep 2
foreach ($p in @("SuavoAgent.Core","SuavoAgent.Broker","SuavoAgent.Helper")) {
    $proc = Get-Process -Name $p -ErrorAction SilentlyContinue
    if ($proc) { Stop-Process -Name $p -Force -ErrorAction SilentlyContinue }
}
Start-Sleep 2
foreach ($s in @("SuavoAgent.Broker","SuavoAgent.Core")) {
    sc.exe delete $s 2>$null | Out-Null
}
Start-Sleep 1

# --- Phase 1: Create directories ---
New-Item $dir -ItemType Directory -Force | Out-Null
New-Item "$data\logs" -ItemType Directory -Force | Out-Null

# --- Phase 2: Download binaries ---
Write-Host "Downloading SuavoAgent v3..." -ForegroundColor Cyan
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
foreach ($f in @("SuavoAgent.Core.exe","SuavoAgent.Broker.exe","SuavoAgent.Helper.exe")) {
    Write-Host "  $f..." -NoNewline
    $dst = Join-Path $dir $f
    try {
        Invoke-WebRequest "$base/$f" -OutFile $dst -UseBasicParsing
        $sizeMb = [math]::Round((Get-Item $dst).Length / 1MB, 1)
        Write-Host " OK ($sizeMb MB)" -ForegroundColor Green
    } catch {
        Write-Host " FAILED" -ForegroundColor Red
        Write-Host "  Error: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "  Check your internet connection and try again." -ForegroundColor Yellow
        exit 1
    }
}

# --- Phase 3: Write config ---
$id = "agent-" + [guid]::NewGuid().ToString("N").Substring(0,12)
$pharmacyId = if ($env:SUAVO_PHARMACY_ID) { $env:SUAVO_PHARMACY_ID } else { "TEST-001" }
$apiKey = if ($env:SUAVO_API_KEY) { $env:SUAVO_API_KEY } else { "" }
$cloudUrl = if ($env:SUAVO_CLOUD_URL) { $env:SUAVO_CLOUD_URL } else { "https://suavollc.com" }

$cfg = @{
    Agent = @{
        CloudUrl = $cloudUrl
        ApiKey = $apiKey
        AgentId = $id
        PharmacyId = $pharmacyId
        Version = "3.0.0"
        LearningMode = $true
    }
} | ConvertTo-Json -Depth 5
Set-Content "$dir\appsettings.json" $cfg -Encoding ASCII
Write-Host "  Config written (Agent: $id, Pharmacy: $pharmacyId)" -ForegroundColor Gray

# Clean stale state from previous installs (DPAPI keys are account-bound)
Remove-Item "$data\state.key" -Force -ErrorAction SilentlyContinue
Remove-Item "$data\state.db" -Force -ErrorAction SilentlyContinue

# --- Phase 4: Set permissions (CRITICAL -- LocalService needs access) ---
Write-Host "Setting permissions..." -ForegroundColor Yellow
icacls $dir /grant "NT AUTHORITY\LocalService:(OI)(CI)M" /T /Q 2>$null
icacls $dir /grant "BUILTIN\Administrators:(OI)(CI)F" /T /Q 2>$null
icacls $data /grant "NT AUTHORITY\LocalService:(OI)(CI)F" /T /Q 2>$null
icacls $data /grant "NT AUTHORITY\SYSTEM:(OI)(CI)F" /T /Q 2>$null
icacls $data /grant "BUILTIN\Administrators:(OI)(CI)F" /T /Q 2>$null
Write-Host "  Permissions set" -ForegroundColor Gray

# --- Phase 5: Register + start services ---
Write-Host "Installing services..." -ForegroundColor Cyan
sc.exe create SuavoAgent.Core binPath= "`"$dir\SuavoAgent.Core.exe`"" start= delayed-auto obj= "NT AUTHORITY\LocalService" | Out-Null
sc.exe description SuavoAgent.Core "Suavo pharmacy agent - core" | Out-Null
sc.exe failure SuavoAgent.Core reset= 3600 actions= restart/5000/restart/30000/restart/60000 | Out-Null
sc.exe create SuavoAgent.Broker binPath= "`"$dir\SuavoAgent.Broker.exe`"" start= delayed-auto obj= LocalSystem | Out-Null
sc.exe description SuavoAgent.Broker "Suavo pharmacy agent - broker" | Out-Null
sc.exe failure SuavoAgent.Broker reset= 3600 actions= restart/5000/restart/30000/restart/60000 | Out-Null
sc.exe config SuavoAgent.Broker depend= SuavoAgent.Core | Out-Null

Write-Host "Starting services..." -ForegroundColor Cyan
Start-Service SuavoAgent.Core -ErrorAction SilentlyContinue
Start-Sleep 3
Start-Service SuavoAgent.Broker -ErrorAction SilentlyContinue
Start-Sleep 2

# --- Phase 6: Verify ---
$coreStatus = (Get-Service SuavoAgent.Core -ErrorAction SilentlyContinue).Status
$brokerStatus = (Get-Service SuavoAgent.Broker -ErrorAction SilentlyContinue).Status

Write-Host ""
if ($coreStatus -eq "Running" -and $brokerStatus -eq "Running") {
    Write-Host "  SUCCESS - SuavoAgent v3 installed and running!" -ForegroundColor Green
} else {
    Write-Host "  PARTIAL - Check service status below" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "  Agent ID:  $id" -ForegroundColor White
Write-Host "  Pharmacy:  $pharmacyId" -ForegroundColor White
Write-Host "  Install:   $dir" -ForegroundColor Gray
Write-Host "  Data:      $data" -ForegroundColor Gray
Write-Host "  Logs:      $data\logs\" -ForegroundColor Gray
Write-Host ""
Get-Service SuavoAgent.* | Format-Table Name, Status -AutoSize
