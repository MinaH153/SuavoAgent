# SuavoAgent — Tier-2 on-device LLM enablement (HIPAA-LOCAL; cloud reasoning stays OFF).
# Run in an ELEVATED PowerShell on the agent box:
#   irm <gist-raw-url> | iex
# Places a local GGUF model + llama.cpp native libs, writes the reasoning config overlay,
# and restarts Core so the brain loads. Nothing leaves the box.
$ErrorActionPreference = "Stop"
Write-Host "=== SuavoAgent Tier-2 (on-device LLM, HIPAA-local) setup ===" -ForegroundColor Cyan

$base   = "C:\ProgramData\SuavoAgent"
$models = Join-Path $base "models"
$native = Join-Path $base "native"
New-Item -ItemType Directory -Force -Path $models, $native | Out-Null
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch {}

# 1) Model — ungated Llama-3.2-1B-Instruct Q4_K_M (~770MB); matches the agent's prompt format.
$modelPath = Join-Path $models "Llama-3.2-1B-Instruct-Q4_K_M.gguf"
$modelUrl  = "https://huggingface.co/unsloth/Llama-3.2-1B-Instruct-GGUF/resolve/main/Llama-3.2-1B-Instruct-Q4_K_M.gguf"
if ((Test-Path $modelPath) -and ((Get-Item $modelPath).Length -gt 500MB)) {
  Write-Host "Model already present — skipping download."
} else {
  Write-Host "Downloading model (~770MB) — slow part, ~2-5 min..."
  try { Start-BitsTransfer -Source $modelUrl -Destination $modelPath -ErrorAction Stop }
  catch { Write-Host "BITS unavailable; using Invoke-WebRequest..."; $ProgressPreference = 'SilentlyContinue'; Invoke-WebRequest $modelUrl -OutFile $modelPath }
}
Write-Host ("Model OK: {0:N0} bytes" -f (Get-Item $modelPath).Length) -ForegroundColor Green

# 2) Native llama.cpp DLLs matching LLamaSharp 0.19.0 (from the CPU backend nupkg).
Write-Host "Downloading llama.cpp native libs (LLamaSharp.Backend.Cpu 0.19.0)..."
$zip = Join-Path $env:TEMP "llamacpp-cpu-0.19.0.zip"
$ProgressPreference = 'SilentlyContinue'
Invoke-WebRequest "https://www.nuget.org/api/v2/package/LLamaSharp.Backend.Cpu/0.19.0" -OutFile $zip
$ex = Join-Path $env:TEMP "llamacpp-cpu-019"
if (Test-Path $ex) { Remove-Item $ex -Recurse -Force }
Expand-Archive $zip $ex -Force
# Prefer avx2 (any modern CPU); fall back to the base no-AVX folder.
$src = Join-Path $ex "runtimes\win-x64\native\avx2"
if (-not (Test-Path (Join-Path $src "llama.dll"))) { $src = Join-Path $ex "runtimes\win-x64\native" }
Copy-Item (Join-Path $src "*.dll") $native -Force
# Belt-and-suspenders: also copy the base-folder DLLs (covers ggml living outside the avx subdir).
Copy-Item (Join-Path $ex "runtimes\win-x64\native\*.dll") $native -Force -ErrorAction SilentlyContinue
Write-Host "Native libs placed:" -ForegroundColor Green
Get-ChildItem $native -Filter *.dll | Select-Object Name, Length | Format-Table -AutoSize

# 3) Reasoning config overlay — Tier-2 ON, Tier-3/cloud OFF. Written directly so the restart
#    picks it up immediately (no sync race); the cloud agent_config_overrides rows keep it on resync.
$cfg = @{ Agent = @{ Reasoning = @{
  Enabled             = $true
  ModelPath           = $modelPath
  NativeLibraryPath   = $native
  ModelId             = "llama-3.2-1b-q4_k_m"
  CloudEnabled        = $false
  PricingBrainEnabled = $false
} } }
$cfgPath = Join-Path $base "config-overrides.json"
[IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json -Depth 6))
Write-Host "Wrote reasoning config overlay: $cfgPath" -ForegroundColor Green

# 4) Restart Core so it rebuilds the reasoning singleton with the new config.
Write-Host "Restarting SuavoAgent.Core..."
Restart-Service "SuavoAgent.Core" -Force
Start-Sleep 8
Get-Service "SuavoAgent.*" | Select-Object Name, Status | Format-Table -AutoSize

Write-Host ("Model SHA256: " + (Get-FileHash -Algorithm SHA256 $modelPath).Hash.ToLower())
Write-Host "=== Done. Tier-2 LOCAL reasoning enabled. Nothing leaves the box. ===" -ForegroundColor Cyan
Write-Host "Verify on dashboard: Agent Fleet -> Hillcrest stays Active, then run a navigate dry-run."
