<#
.SYNOPSIS
  FSD Eval driver — drives the PioneerRx sim through a known task N times so a LIVE
  SuavoAgent (LearningMode=true) observes + learns it, then records the ground-truth
  trajectory for grade.mjs to score against.

.DESCRIPTION
  This is an OPERATOR STAND-IN, not the agent's executor. It drives the sim via UIA
  InvokePattern/SelectionItemPattern, which fire the exact InvokedEvent/FocusChanged the
  live Helper's UiaInteractionObserver listens for. See tools/FsdEval/README.md.

  Requires: Windows, the console (not RDP) session, .NET 8 SDK (only if building the sim),
  and a live installed SuavoAgent attached to process "PioneerPharmacy".

.EXAMPLE
  pwsh tools/FsdEval/Invoke-FsdEval.ps1 -Reps 6 -Out C:\ProgramData\SuavoAgent\fsd-eval-run.json
#>
[CmdletBinding()]
param(
    [int]$Reps = 6,
    [string]$Out = "fsd-eval-run.json",
    [string]$SimPath = "",                      # pre-built PioneerPharmacy.exe; else auto-build
    [ValidateSet("faithful")] [string]$Variant = "faithful",
    [int]$StepPauseMs = 1500,                   # < MaxEdgeGap (30s) so steps stay one routine
    [int]$RepPauseMs = 2500,
    [switch]$KeepSimOpen
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$task = "pricing-nav"

# Ground truth: the trajectory this driver performs (what a correct observation must capture).
$expected = @(
    [ordered]@{ step = 1; action = "expand"; controlType = "MenuItem"; name = "Item"    }
    [ordered]@{ step = 2; action = "invoke"; controlType = "MenuItem"; name = "Rx Item"  }
    [ordered]@{ step = 3; action = "select"; controlType = "TabItem";  name = "Pricing"  }
)
$expectedEndState = "window 'Edit Rx Item' open, 'Pricing' tab selected, pricing DataGrid visible"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

function Write-Phase($m) { Write-Host "`n== $m ==" -ForegroundColor Cyan }
function Write-Ok($m)    { Write-Host "  [OK] $m" -ForegroundColor Green }
function Write-Warn2($m) { Write-Host "  [WARN] $m" -ForegroundColor Yellow }

# ── Resolve / build the sim ────────────────────────────────────────────────────────────────
if (-not $SimPath) {
    $staged = Join-Path $repoRoot ".rehearsal-stage\Sim\PioneerPharmacy.exe"
    if (Test-Path $staged) {
        $SimPath = $staged
    } else {
        Write-Phase "Building PioneerRxSim (WPF, Windows-only)"
        $outDir = Join-Path $repoRoot ".rehearsal-stage\Sim"
        dotnet publish (Join-Path $repoRoot "tools\PioneerRxSim\PioneerRxSim.csproj") `
            -c Release -r win-x64 --self-contained false -o $outDir
        if ($LASTEXITCODE -ne 0) { throw "PioneerRxSim publish failed" }
        $SimPath = Join-Path $outDir "PioneerPharmacy.exe"
    }
}
if (-not (Test-Path $SimPath)) { throw "Sim not found at $SimPath" }
Write-Ok "Sim: $SimPath"

# ── Launch the sim; the live Helper attaches by process name "PioneerPharmacy" ───────────────
Write-Phase "Launching sim + waiting for main window"
$sim = Start-Process -FilePath $SimPath -ArgumentList "--variant", $Variant -PassThru
Start-Sleep -Milliseconds 1500

$root = [System.Windows.Automation.AutomationElement]::RootElement
function Find-ByNameType($parent, $name, $ctId, [int]$timeoutMs = 8000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ctId)))
    do {
        $el = $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($el) { return $el }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    return $null
}

$ct = [System.Windows.Automation.ControlType]
# The window title has a long "[SIMULATOR ...]" suffix, so match by ProcessId (UIA
# PropertyCondition does NOT support wildcards) rather than by Name.
$byPid = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $sim.Id)
$mainWin = $null
$deadline = (Get-Date).AddMilliseconds(8000)
do {
    $mainWin = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $byPid)
    if ($mainWin) { break }
    Start-Sleep -Milliseconds 250
} while ((Get-Date) -lt $deadline)
if (-not $mainWin) { throw "Sim main window not found via UIA (pid $($sim.Id))" }
Write-Ok "Main window: $($mainWin.Current.Name)"

function Invoke-El($el)  { ($el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
function Expand-El($el)  { ($el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand() }
function Select-El($el)  { ($el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() }

# ── Drive the known task N times ─────────────────────────────────────────────────────────────
Write-Phase "Driving '$task' x$Reps (steps ${StepPauseMs}ms apart, reps ${RepPauseMs}ms apart)"
$repLog = @()
for ($i = 1; $i -le $Reps; $i++) {
    try {
        $itemMenu = Find-ByNameType $mainWin "Item" $ct::MenuItem 5000
        if (-not $itemMenu) { throw "menu 'Item' not found" }
        Expand-El $itemMenu
        Start-Sleep -Milliseconds $StepPauseMs

        $rxItem = Find-ByNameType $root "Rx Item" $ct::MenuItem 5000   # submenu is a popup off-root
        if (-not $rxItem) { throw "'Rx Item' not found" }
        Invoke-El $rxItem
        Start-Sleep -Milliseconds $StepPauseMs

        $editWin = Find-ByNameType $root "Edit Rx Item" $ct::Window 6000
        if (-not $editWin) { throw "'Edit Rx Item' window did not open" }
        $pricing = Find-ByNameType $editWin "Pricing" $ct::TabItem 5000
        if (-not $pricing) { throw "'Pricing' tab not found" }
        Select-El $pricing
        Start-Sleep -Milliseconds $StepPauseMs

        # verify end-state: Pricing tab selected
        $sel = ($pricing.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected
        # PS 5.1 can't parse an `if` statement as a hashtable value — keep it a plain expression
        $endStateTxt = if ($sel) { "Pricing selected" } else { "tab not selected" }
        $repLog += [ordered]@{ rep = $i; ok = [bool]$sel; endState = $endStateTxt }
        Write-Ok "rep ${i}: Item -> Rx Item -> Pricing (selected=$sel)"

        # reset: close Edit Rx Item so the next rep re-opens it (sim binds Escape -> close)
        try { ($editWin.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close() }
        catch { [System.Windows.Forms.SendKeys]::SendWait("{ESC}") }
        Start-Sleep -Milliseconds $RepPauseMs
    }
    catch {
        Write-Warn2 "rep ${i} failed: $($_.Exception.Message)"
        $repLog += [ordered]@{ rep = $i; ok = $false; endState = "error: $($_.Exception.Message)" }
        try { [System.Windows.Forms.SendKeys]::SendWait("{ESC}") } catch {}
        Start-Sleep -Milliseconds $RepPauseMs
    }
}

if (-not $KeepSimOpen) {
    Write-Phase "Closing sim"
    try { $sim.CloseMainWindow() | Out-Null; Start-Sleep 1; if (-not $sim.HasExited) { $sim.Kill() } } catch {}
}

# ── Emit ground-truth manifest for grade.mjs ─────────────────────────────────────────────────
$okReps = ($repLog | Where-Object { $_.ok }).Count
$manifest = [ordered]@{
    task              = $task
    variant           = $Variant
    reps              = $Reps
    repsOk            = $okReps
    stepsPerRep       = $expected.Count
    expectedTrajectory = $expected
    expectedEndState  = $expectedEndState
    expectedInteractionDelta = $okReps * $expected.Count
    minFrequency      = 5
    reps_detail       = $repLog
    driverFinishedAt  = (Get-Date).ToUniversalTime().ToString("o")
    baseline          = $null   # grade.mjs fills if absent (snapshot at first score call)
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $Out -Encoding utf8
Write-Ok "Ground truth written: $Out  ($okReps/$Reps reps clean, expected +$($manifest.expectedInteractionDelta) interactions)"
Write-Host "`nNext: node tools/FsdEval/grade.mjs --run $Out --agent <agentId>" -ForegroundColor Cyan
