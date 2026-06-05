# Fix Tier-2 natives: place a CLEAN, single-variant avx2 set (consistent llama+ggml+llava).
# Root cause (2026-06-05): setup-tier2.ps1 copied avx2\*.dll THEN base native\*.dll over it,
# leaving an inconsistent/dispatcher set -> model loads but grammar-constrained generation yields
# 0 tokens. Reasoning code/grammar/prompt are PROVEN correct (valid proposal on a Mac with the
# package's osx natives). Must stop Core (+ Watchdog so it won't respawn) to release the DLL lock.
$ErrorActionPreference = 'Stop'
Write-Host '=== Fix Tier-2 natives (clean avx2) ===' -ForegroundColor Cyan
$native = 'C:\ProgramData\SuavoAgent\native'
New-Item -ItemType Directory -Force -Path $native | Out-Null

# 1) Download + extract FIRST (minimize downtime).
$zip = Join-Path $env:TEMP 'llamacpp-cpu-019fix.zip'
$ex  = Join-Path $env:TEMP 'llamacpp-cpu-019fix'
if (Test-Path $ex) { Remove-Item $ex -Recurse -Force }
$ProgressPreference = 'SilentlyContinue'
Invoke-WebRequest 'https://www.nuget.org/api/v2/package/LLamaSharp.Backend.Cpu/0.19.0' -OutFile $zip
Expand-Archive $zip $ex -Force
$src = Join-Path $ex 'runtimes\win-x64\native\avx2'
if (-not (Test-Path (Join-Path $src 'llama.dll'))) { throw 'avx2 natives not found in nupkg' }

# 2) Stop Watchdog first (so it will NOT auto-respawn Core), then Core (releases the native lock).
Write-Host 'Stopping services to release the native DLL lock...' -ForegroundColor Yellow
Stop-Service suavoagent.watchdog -Force -ErrorAction SilentlyContinue
Stop-Service suavoagent.core -Force -ErrorAction SilentlyContinue   # also stops dependent Broker
Start-Sleep 3
Get-Process -Name 'suavoagent.core','suavoagent.broker','suavoagent.helper' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep 3

# 3) Replace with ONLY the clean avx2 set.
Remove-Item (Join-Path $native '*.dll') -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $src '*.dll') $native -Force
Write-Host 'Clean avx2 native set placed:' -ForegroundColor Green
Get-ChildItem $native -Filter *.dll | Select-Object Name, Length | Format-Table -AutoSize

# 4) Start in dependency order: Core -> Broker -> Watchdog.
Start-Service suavoagent.core
Start-Sleep 10
Start-Service suavoagent.broker -ErrorAction SilentlyContinue
Start-Sleep 3
Start-Service suavoagent.watchdog -ErrorAction SilentlyContinue
Start-Sleep 10
Get-Service suavoagent.core, suavoagent.broker, suavoagent.watchdog | Select-Object Name, Status | Format-Table -AutoSize
Write-Host '=== Done. Re-run a navigate dry-run to validate Tier-2 output. ===' -ForegroundColor Cyan
