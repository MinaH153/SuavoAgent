# bench/box/a1.ps1 — Arm A1 (shipped PricingWorkflow / UiaFirst) conformance run.
# Wraps tools/PioneerRxRehearsal/rehearsal.ps1, captures all output + the exit code to a
# file so the result is readable over CRD with `get-content` (no Shift chars needed there).
#
#   pwsh bench\box\a1.ps1 -createmarker            # first run (admin): marker + build + faithful
#   pwsh bench\box\a1.ps1 -variant renamed-cost    # subsequent variant (build cached)
#   pwsh bench\box\a1.ps1 -variant faithful -skipbuild
#
# Exit/oracle (from rehearsal): 0 = conformance pass, 2 = bug found (real workflow defect on a
# faithful sim — what we want surfaced), 3 = chain/setup failure, 64 = bad args.
param(
    [ValidateSet("faithful","renamed-cost","slow-grid","glacial-grid","wpf-menu","currency-cells","virtual-depth")]
    [string]$Variant = "faithful",
    [switch]$CreateMarker,
    [switch]$SkipBuild
)
$ErrorActionPreference = "Continue"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$outDir = Join-Path $repo "bench\box\out"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $outDir ("a1-" + $Variant + "-" + $stamp + ".out")
$reh = Join-Path $repo "tools\PioneerRxRehearsal\rehearsal.ps1"

$rehArgs = @("-Variant", $Variant)
if ($CreateMarker) { $rehArgs += "-CreatePmsMarker" }
if ($SkipBuild)    { $rehArgs += "-SkipBuild" }

"=== A1 rehearsal | variant=$Variant | $stamp ===" | Tee-Object -FilePath $out
"repo=$repo" | Tee-Object -FilePath $out -Append
"args=$($rehArgs -join ' ')" | Tee-Object -FilePath $out -Append

# Run rehearsal in a child pwsh so its trailing `exit` doesn't kill this wrapper; capture all streams.
& pwsh -NoProfile -ExecutionPolicy Bypass -File $reh @rehArgs *>&1 | Tee-Object -FilePath $out -Append
$code = $LASTEXITCODE

$verdict = switch ($code) {
    0 { "PASS (conformance)" }
    2 { "BUG FOUND (workflow defect on faithful sim)" }
    3 { "CHAIN/SETUP FAIL" }
    64 { "BAD ARGS" }
    default { "UNKNOWN($code)" }
}
"REHEARSAL_EXIT=$code" | Tee-Object -FilePath $out -Append
"VERDICT=$verdict" | Tee-Object -FilePath $out -Append
"OUTFILE=$out" | Tee-Object -FilePath $out -Append
Write-Host ""
Write-Host ("A1 " + $Variant + " -> exit=" + $code + " (" + $verdict + ")")
Write-Host ("log: " + $out)
exit $code
