# SuavoAgent - Tier-2 on-device LLM enablement (HIPAA-LOCAL; cloud reasoning stays OFF).
# Run in an ELEVATED PowerShell on the agent box:
#   irm <raw-url> -OutFile s.ps1 ; . .\s.ps1
# Places a local GGUF model + llama.cpp native libs, writes the reasoning config overlay,
# and restarts Core so the brain loads. Nothing leaves the box.
$ErrorActionPreference = "Stop"
Write-Host "=== SuavoAgent Tier-2 (on-device LLM, HIPAA-local) setup ===" -ForegroundColor Cyan

# Force a modern TLS - Windows PowerShell 5.1 can default to TLS 1.0/1.1 which HuggingFace rejects,
# which silently breaks large downloads. Set it unconditionally (NOT inside a swallowing try/catch).
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$base   = "C:\ProgramData\SuavoAgent"
$models = Join-Path $base "models"
$native = Join-Path $base "native"
New-Item -ItemType Directory -Force -Path $models, $native | Out-Null

# Reliable large-file download. curl.exe (built into Windows 10/11) follows HF's redirect to the xet
# CDN and negotiates modern TLS far better than PS 5.1's WebClient (which connects via TCP but fails
# the TLS/redirect handshake to cas-bridge.xethub.hf.co, producing no file + no visible error).
# Falls back to WebClient then BITS. Surfaces the REAL error instead of dying silently.
function Get-RemoteFile([string]$url, [string]$dest) {
    Write-Host "  downloading -> $dest" -ForegroundColor Gray
    $curl = Join-Path $env:SystemRoot "System32\curl.exe"
    if (Test-Path $curl) {
        & $curl -L --fail --retry 3 --connect-timeout 30 -A "Mozilla/5.0" -o $dest $url
        if (($LASTEXITCODE -eq 0) -and (Test-Path $dest)) { return }
        Write-Host "  curl exit $LASTEXITCODE - falling back to WebClient..." -ForegroundColor Yellow
    }
    $wc = New-Object System.Net.WebClient
    $wc.Headers.Add('User-Agent', 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)')
    try {
        $wc.DownloadFile($url, $dest)
    } catch {
        Write-Host ("  WebClient failed: " + $_.Exception.Message) -ForegroundColor Yellow
        if ($_.Exception.InnerException) { Write-Host ("  inner: " + $_.Exception.InnerException.Message) -ForegroundColor Yellow }
        Write-Host "  retrying via BITS..." -ForegroundColor Yellow
        Start-BitsTransfer -Source $url -Destination $dest
    } finally {
        $wc.Dispose()
    }
}

# 1) Model - TinyLlama-1.1B-Chat Q4_K_M (~668MB), ungated. CI #175 proved this exact model produces
#    valid grammar-constrained proposals with this stack. Llama-3.2-1B emitted its <|eot_id|> stop
#    token immediately (0 output tokens) because the agent's AntiPrompts are Llama-3 stop tokens;
#    TinyLlama doesn't use those, so it generates under the GBNF grammar instead of halting.
$modelPath = Join-Path $models "tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf"
$modelUrl  = "https://huggingface.co/TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF/resolve/main/tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf"
if ((Test-Path $modelPath) -and ((Get-Item $modelPath).Length -gt 500MB)) {
    Write-Host "Model already present - skipping download." -ForegroundColor Green
} else {
    Write-Host "Downloading model (~770MB) - slow part, ~2-5 min..."
    Get-RemoteFile $modelUrl $modelPath
}
if (-not (Test-Path $modelPath) -or ((Get-Item $modelPath).Length -lt 100MB)) {
    throw "Model download did not produce a valid file at $modelPath (check connectivity to huggingface.co)."
}
Write-Host ("Model OK: {0:N0} bytes" -f (Get-Item $modelPath).Length) -ForegroundColor Green

# 2) Native llama.cpp DLLs matching LLamaSharp 0.19.0 (from the CPU backend nupkg).
$llamaDll = Join-Path $native "llama.dll"
if (Test-Path $llamaDll) {
    Write-Host "Native libs already present - skipping." -ForegroundColor Green
} else {
    Write-Host "Downloading llama.cpp native libs (LLamaSharp.Backend.Cpu 0.19.0)..."
    $zip = Join-Path $env:TEMP "llamacpp-cpu-0.19.0.zip"
    Get-RemoteFile "https://www.nuget.org/api/v2/package/LLamaSharp.Backend.Cpu/0.19.0" $zip
    $ex = Join-Path $env:TEMP "llamacpp-cpu-019"
    if (Test-Path $ex) { Remove-Item $ex -Recurse -Force }
    Expand-Archive $zip $ex -Force
    # Prefer avx2 (any modern CPU); fall back to the base no-AVX folder.
    $src = Join-Path $ex "runtimes\win-x64\native\avx2"
    if (-not (Test-Path (Join-Path $src "llama.dll"))) { $src = Join-Path $ex "runtimes\win-x64\native" }
    Copy-Item (Join-Path $src "*.dll") $native -Force
    # Belt-and-suspenders: also copy the base-folder DLLs (covers ggml living outside the avx subdir).
    Copy-Item (Join-Path $ex "runtimes\win-x64\native\*.dll") $native -Force -ErrorAction SilentlyContinue
}
if (-not (Test-Path $llamaDll)) { throw "Native libs missing - llama.dll not placed at $native." }
Write-Host "Native libs placed:" -ForegroundColor Green
Get-ChildItem $native -Filter *.dll | Select-Object Name, Length | Format-Table -AutoSize

# 3) Reasoning config overlay - Tier-2 ON, Tier-3/cloud OFF. Written directly so the restart picks it
#    up immediately (no sync race); the cloud agent_config_overrides rows keep it across resyncs.
$cfg = @{ Agent = @{ Reasoning = @{
    Enabled             = $true
    ModelPath           = $modelPath
    NativeLibraryPath   = $native
    ModelId             = "tinyllama-1.1b-chat"
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
