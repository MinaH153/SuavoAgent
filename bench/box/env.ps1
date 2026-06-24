# bench/box/env.ps1 — print the box environment for the benchmark report (CPU, AVX2, SDK, git).
$ErrorActionPreference = "Continue"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$outDir = Join-Path $repo "bench\box\out"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$out = Join-Path $outDir "env.out"

$lines = @()
$lines += "=== box env | $(Get-Date -Format o) ==="
$lines += "dotnet=$(dotnet --version 2>&1)"
$lines += "pwsh=$($PSVersionTable.PSVersion)"
$lines += "git_head=$(git -C $repo rev-parse --short HEAD 2>&1)"
$lines += "git_tag=$(git -C $repo describe --tags --exact-match 2>$null)"
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$lines += "cpu_name=$($cpu.Name)"
$lines += "cpu_cores=$($cpu.NumberOfCores) cpu_logical=$($cpu.NumberOfLogicalProcessors)"
$os = Get-CimInstance Win32_OperatingSystem
$ramGb = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
$lines += "ram_gb=$ramGb os=$($os.Caption) build=$($os.BuildNumber)"
# AVX2 capability gates the fast llama backend (NOAVX is ~5-10x slower for Qwen3).
$avx2 = [System.Runtime.Intrinsics.X86.Avx2]::IsSupported
$avx = [System.Runtime.Intrinsics.X86.Avx]::IsSupported
$lines += "avx=$avx avx2=$avx2"

$lines | Tee-Object -FilePath $out
Write-Host ""
Write-Host "env written: $out"
