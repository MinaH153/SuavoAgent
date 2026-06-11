# PioneerRx dress rehearsal — build, stage, and run on the Windows VM.
#
#   pwsh tools/PioneerRxRehearsal/rehearsal.ps1 -CreatePmsMarker          # first run (admin)
#   pwsh tools/PioneerRxRehearsal/rehearsal.ps1                           # quick faithful run
#   pwsh tools/PioneerRxRehearsal/rehearsal.ps1 -Variant virtual-depth    # the UIA2 virtualization answer
#   pwsh tools/PioneerRxRehearsal/rehearsal.ps1 -Mode full                # + REAL file discovery
#
# Stage layout (the Helper-side VerifyClientIsCore gate requires this exact shape —
# client must be SuavoAgent.Core.exe under the Helper's install ROOT):
#   <stage>\Helper\SuavoAgent.Helper.exe      (REAL helper, published win-x64)
#   <stage>\Core\SuavoAgent.Core.exe          (rehearsal driver, renamed apphost)
#   <stage>\Sim\PioneerPharmacy.exe           (the simulator; engine attaches by process name)

param(
    [ValidateSet("quick", "full")] [string]$Mode = "quick",
    [ValidateSet("faithful", "renamed-cost", "slow-grid", "glacial-grid", "wpf-menu", "currency-cells", "virtual-depth")]
    [string]$Variant = "faithful",
    [switch]$ClearSearchAfterLoad,
    [switch]$NoPersistLastItem,
    [switch]$SkipBuild,
    [switch]$CreatePmsMarker,
    [switch]$KeepOpen,
    [switch]$StageOnly,
    [int]$ThrottleMs = 1500,
    [string]$StageDir = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if ([string]::IsNullOrEmpty($StageDir)) { $StageDir = Join-Path $repoRoot ".rehearsal-stage" }

Write-Host "Repo root : $repoRoot"
Write-Host "Stage dir : $StageDir"

# ── PioneerRx install marker (the REAL Helper's PioneerRxInstallDetector gates its attach
#    loop on this; without it the Helper parks and never attaches to the sim) ──────────────
$markerDir = "C:\Program Files (x86)\New Tech Computer Systems\PioneerRx"
$marker = Join-Path $markerDir "PioneerPharmacy.exe"
if ($CreatePmsMarker) {
    if (-not (Test-Path $marker)) {
        New-Item -ItemType Directory -Force -Path $markerDir | Out-Null
        Set-Content -Path $marker -Value "rehearsal marker - not a real binary" -Encoding ascii
        Write-Host "Created PMS install marker: $marker"
    } else {
        Write-Host "PMS install marker already present: $marker"
    }
}
if (-not (Test-Path $marker)) {
    Write-Warning "PMS install marker missing ($marker). Run once as admin with -CreatePmsMarker or the Helper will never attach."
}

# ── Build + stage ──────────────────────────────────────────────────────────────────────────
if (-not $SkipBuild) {
    Write-Host "`n[1/4] Publishing REAL Helper (win-x64, production publish shape)..."
    dotnet publish (Join-Path $repoRoot "src\SuavoAgent.Helper\SuavoAgent.Helper.csproj") `
        -c Release -r win-x64 --self-contained false -o (Join-Path $StageDir "Helper")
    if ($LASTEXITCODE -ne 0) { throw "Helper publish failed" }

    Write-Host "`n[2/4] Publishing PioneerRxSim (WPF, builds only on Windows)..."
    dotnet publish (Join-Path $repoRoot "tools\PioneerRxSim\PioneerRxSim.csproj") `
        -c Release -o (Join-Path $StageDir "Sim")
    if ($LASTEXITCODE -ne 0) { throw "Sim publish failed" }

    Write-Host "`n[3/4] Publishing rehearsal driver..."
    dotnet publish (Join-Path $repoRoot "tools\PioneerRxRehearsal\PioneerRxRehearsal.csproj") `
        -c Release -o (Join-Path $StageDir "Core")
    if ($LASTEXITCODE -ne 0) { throw "Rehearsal publish failed" }

    # Rename the apphost so the Helper's client-verification gate (ProcessName must be
    # SuavoAgent.Core, exe under the install root) passes EXACTLY as in production.
    # The apphost binds to PioneerRxRehearsal.dll by embedded name — renaming the exe is safe.
    Write-Host "`n[4/4] Staging apphost as SuavoAgent.Core.exe..."
    Copy-Item (Join-Path $StageDir "Core\PioneerRxRehearsal.exe") `
              (Join-Path $StageDir "Core\SuavoAgent.Core.exe") -Force
}

if ($StageOnly) { Write-Host "`nStaged. Run: $StageDir\Core\SuavoAgent.Core.exe --help"; exit 0 }

# ── Run ────────────────────────────────────────────────────────────────────────────────────
$driver = Join-Path $StageDir "Core\SuavoAgent.Core.exe"
$argsList = @("--mode", $Mode, "--variant", $Variant, "--stage", $StageDir, "--throttle-ms", $ThrottleMs)
if ($ClearSearchAfterLoad) { $argsList += "--clear-search-after-load" }
if ($NoPersistLastItem) { $argsList += "--no-persist-last-item" }
if ($KeepOpen) { $argsList += "--keep-open" }

Write-Host "`nLaunching: $driver $($argsList -join ' ')`n"
& $driver @argsList
exit $LASTEXITCODE
