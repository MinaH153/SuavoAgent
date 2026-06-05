# Fix Tier-2 natives: place a CLEAN, single-variant avx2 set (consistent llama+ggml+llava).
# Root cause (2026-06-05): setup-tier2.ps1 copied avx2\*.dll THEN base native\*.dll over it,
# leaving an inconsistent/dispatcher set -> model loads but grammar-constrained generation yields
# 0 tokens. Reasoning code/grammar/prompt are PROVEN correct (valid proposal on a Mac with the
# package's osx natives). This places ONLY the self-contained avx2 set, then restarts.
$ErrorActionPreference = 'Stop'
Write-Host '=== Fix Tier-2 natives (clean avx2) ===' -ForegroundColor Cyan
$native = 'C:\ProgramData\SuavoAgent\native'
New-Item -ItemType Directory -Force -Path $native | Out-Null
$zip = Join-Path $env:TEMP 'llamacpp-cpu-019fix.zip'
$ex  = Join-Path $env:TEMP 'llamacpp-cpu-019fix'
if (Test-Path $ex) { Remove-Item $ex -Recurse -Force }
$ProgressPreference = 'SilentlyContinue'
Invoke-WebRequest 'https://www.nuget.org/api/v2/package/LLamaSharp.Backend.Cpu/0.19.0' -OutFile $zip
Expand-Archive $zip $ex -Force
$src = Join-Path $ex 'runtimes\win-x64\native\avx2'
if (-not (Test-Path (Join-Path $src 'llama.dll'))) { throw 'avx2 natives not found in nupkg' }
Remove-Item (Join-Path $native '*.dll') -Force -ErrorAction SilentlyContinue   # CLEAN first
Copy-Item (Join-Path $src '*.dll') $native -Force                              # ONLY avx2 set
Write-Host 'Clean avx2 native set placed:' -ForegroundColor Green
Get-ChildItem $native -Filter *.dll | Select-Object Name, Length | Format-Table -AutoSize
Restart-Service suavoagent.core -Force
Start-Sleep 12
Start-Service suavoagent.broker -ErrorAction SilentlyContinue
Start-Sleep 12
Get-Service suavoagent.core, suavoagent.broker, suavoagent.watchdog | Select-Object Name, Status | Format-Table -AutoSize
Write-Host '=== Done. Re-run a navigate dry-run to validate Tier-2 output. ===' -ForegroundColor Cyan
