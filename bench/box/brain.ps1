# bench/box/brain.ps1 — Arm A2/A3 foundation: prove the Qwen3-1.7B local brain loads + runs on
# this box's CPU, and capture load/inference latency. Downloads the GGUF (~1.1GB) if absent.
#
#   pwsh bench\box\brain.ps1                 # download model if needed, run the smoke test
#   pwsh bench\box\brain.ps1 -skipdownload   # assume model already present
#
# Model filename MUST contain "Qwen3-1.7B-Q4_K_M" so InferencePromptBuilder.ResolveFormat picks
# the Qwen3Thinkless chat format (empty <think> prefill). LLamaSharp 0.24 + Backend.Cpu supply
# the native libs; AVX2 vs NOAVX is selected at runtime (see env.ps1 for which this box has).
param([switch]$SkipDownload)
$ErrorActionPreference = "Continue"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$outDir = Join-Path $repo "bench\box\out"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$out = Join-Path $outDir "brain.out"
$modelDir = Join-Path $repo "bench\models"
New-Item -ItemType Directory -Force -Path $modelDir | Out-Null
$model = Join-Path $modelDir "Qwen_Qwen3-1.7B-Q4_K_M.gguf"
$url = "https://huggingface.co/bartowski/Qwen_Qwen3-1.7B-GGUF/resolve/main/Qwen_Qwen3-1.7B-Q4_K_M.gguf"

"=== brain | $(Get-Date -Format o) ===" | Tee-Object -FilePath $out

if (-not $SkipDownload -and -not (Test-Path $model)) {
    "downloading GGUF (~1.1GB) ..." | Tee-Object -FilePath $out -Append
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    # curl.exe ships with Windows 10/11 and is far faster than Invoke-WebRequest for large files.
    & curl.exe -L --fail --retry 3 -o $model $url
    $sw.Stop()
    "download_exit=$LASTEXITCODE elapsed_s=$([math]::Round($sw.Elapsed.TotalSeconds,1))" | Tee-Object -FilePath $out -Append
}
if (-not (Test-Path $model)) { "ERROR: model missing at $model" | Tee-Object -FilePath $out -Append; exit 3 }
$sizeMb = [math]::Round((Get-Item $model).Length / 1MB, 1)
"model=$model size_mb=$sizeMb" | Tee-Object -FilePath $out -Append

# The smoke test (LocalLlmSmokeTests) is gated on SUAVOAGENT_TEST_GGUF; it loads the model and
# asserts a real generation. We time the whole test run; the test logs load + inference stopwatches.
$env:SUAVOAGENT_TEST_GGUF = $model
$proj = Join-Path $repo "tests\SuavoAgent.Helper.Tests\SuavoAgent.Helper.Tests.csproj"
"running LocalLlmSmokeTests (SUAVOAGENT_TEST_GGUF set) ..." | Tee-Object -FilePath $out -Append
$sw2 = [System.Diagnostics.Stopwatch]::StartNew()
& dotnet test $proj -c Release --filter "FullyQualifiedName~LocalLlmSmokeTests" --logger "console;verbosity=detailed" *>&1 | Tee-Object -FilePath $out -Append
$code = $LASTEXITCODE
$sw2.Stop()
"SMOKE_EXIT=$code elapsed_s=$([math]::Round($sw2.Elapsed.TotalSeconds,1))" | Tee-Object -FilePath $out -Append
Write-Host ""
Write-Host ("brain smoke -> exit=" + $code + " (0=pass) | log: " + $out)
exit $code
