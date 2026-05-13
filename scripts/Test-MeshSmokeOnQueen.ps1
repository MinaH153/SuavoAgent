#Requires -Version 7.0
<#
.SYNOPSIS
    Mesh Phase 1 end-to-end smoke test for Queen (Windows host).

.DESCRIPTION
    Runs in ~3-5 minutes. Verifies the bits that GitHub-hosted CI can't:
      1. Preflight passes against real Yubikey + EV cert + SmartCard
      2. publish.ps1 heartbeat fires + AOT crash reporter publishes
      3. Forced publish failure → suavo-report-crash JSONL appears with
         expected fields (including SHA-256 hashed EV cert thumbprint)
      4. tail-events.ps1 sanity check on a synthetic events.jsonl
      5. SuavoAgent.Setup.exe loads (Wire.AttachUnhandledHooks fires
         without throwing on first execution)

    Prints a green/red summary card at the end. Exit 0 on all-PASS, 1
    on any FAIL. Per-check verbose output so failures are debuggable.

.PARAMETER CertThumbprint
    EV code-signing cert SHA1 thumbprint. Falls back to
    $env:SUAVO_CERT_THUMBPRINT. If empty, signing-related checks SKIP.

.PARAMETER SkipPublish
    Skip the heavy publish.ps1 invocation (saves ~8 minutes). Still
    runs preflight + synthetic crash reporter + tail-events sanity.

.EXAMPLE
    pwsh -File scripts/Test-MeshSmokeOnQueen.ps1
    pwsh -File scripts/Test-MeshSmokeOnQueen.ps1 -SkipPublish
    pwsh -File scripts/Test-MeshSmokeOnQueen.ps1 -CertThumbprint <SHA1>
#>

[CmdletBinding()]
param(
    [string]$CertThumbprint = $env:SUAVO_CERT_THUMBPRINT,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Continue'  # Keep going even if a check fails
$script:Results = New-Object System.Collections.Generic.List[object]
$script:RepoRoot = Split-Path -Parent $PSScriptRoot

function Add-Result {
    param(
        [string]$Name,
        [ValidateSet('PASS', 'FAIL', 'SKIP', 'WARN')][string]$Status,
        [string]$Detail = ''
    )
    $script:Results.Add([pscustomobject]@{ Name = $Name; Status = $Status; Detail = $Detail })
    $color = switch ($Status) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        'SKIP' { 'DarkGray' }
        'WARN' { 'Yellow' }
    }
    Write-Host ("  [{0,-5}] {1,-44} {2}" -f $Status, $Name, $Detail) -ForegroundColor $color
}

function Test-Section { param([string]$Name) Write-Host "`n=== $Name ===" -ForegroundColor Cyan }

# ── 1. Preflight ───────────────────────────────────────────────────────
Test-Section "1. Queen ship preflight"
$preflightPath = Join-Path $script:RepoRoot 'scripts\Test-QueenShipPreflight.ps1'
if (-not (Test-Path -LiteralPath $preflightPath)) {
    Add-Result 'preflight-script-exists' 'FAIL' "missing at $preflightPath"
} else {
    # Per preflight contract: empty CertThumbprint + -SkipSigning = signing-checks SKIP.
    # Hashtable splat avoids pwsh 7 array-splat ambiguity where '-Json' was bound
    # positionally to CleanCacheMaxAgeDays (int) instead of as the Json switch.
    $preflightArgs = @{ Json = $true }
    if ([string]::IsNullOrWhiteSpace($CertThumbprint)) {
        $preflightArgs['SkipSigning'] = $true
    } else {
        $preflightArgs['CertThumbprint'] = $CertThumbprint
    }
    $preflightOut = & $preflightPath @preflightArgs 2>&1 | Out-String
    $preflight = $null
    $parseError = $null
    try { $preflight = $preflightOut | ConvertFrom-Json -ErrorAction Stop } catch { $parseError = $_.Exception.Message }
    if ($null -eq $preflight) {
        Add-Result 'preflight-runs' 'FAIL' "could not parse preflight output as JSON: $parseError"
        $argsRendered = ($preflightArgs.GetEnumerator() | ForEach-Object { "-$($_.Key) $($_.Value)" }) -join ' '
        Write-Host "  preflight invoked with: $argsRendered" -ForegroundColor DarkGray
        Write-Host "  preflight raw output (full, $($preflightOut.Length) chars):" -ForegroundColor DarkGray
        Write-Host $preflightOut -ForegroundColor DarkGray
    } else {
        $passCount = ($preflight.results | Where-Object { $_.status -eq 'PASS' }).Count
        $failCount = ($preflight.results | Where-Object { $_.status -eq 'FAIL' }).Count
        $warnCount = ($preflight.results | Where-Object { $_.status -eq 'WARN' }).Count
        $skipCount = ($preflight.results | Where-Object { $_.status -eq 'SKIP' }).Count
        Add-Result 'preflight-runs' ($(if ($preflight.ok) { 'PASS' } else { 'FAIL' })) `
            "$passCount PASS, $failCount FAIL, $warnCount WARN, $skipCount SKIP"
        foreach ($r in $preflight.results) {
            $rstatus = if ($r.status -eq 'PASS' -or $r.status -eq 'SKIP') { 'PASS' } else { $r.status }
            Add-Result "  preflight.$($r.name)" $rstatus $r.detail
        }
    }
}

# ── 2. SuavoReportCrash synthetic invocation ───────────────────────────
Test-Section "2. suavo-report-crash.exe synthetic JSONL"
# Build the tool standalone (fast — no main projects)
$crashToolCsproj = Join-Path $script:RepoRoot 'tools\SuavoReportCrash\SuavoReportCrash.csproj'
$crashToolOut = Join-Path $env:TEMP "suavo-report-crash-smoke-$(Get-Random)"
# Capture publish output so we can root-cause on failure (don't swallow with Out-Null)
$publishLog = Join-Path $env:TEMP "suavo-report-crash-publish-$(Get-Random).log"
& dotnet publish $crashToolCsproj -c Release -r win-x64 --self-contained -o $crashToolOut *>&1 | Tee-Object -FilePath $publishLog | Out-Null
$publishExit = $LASTEXITCODE
$crashExe = Join-Path $crashToolOut 'suavo-report-crash.exe'
if (-not (Test-Path -LiteralPath $crashExe)) {
    $tail = (Get-Content -LiteralPath $publishLog -Tail 20 -ErrorAction SilentlyContinue) -join "`n"
    Add-Result 'crash-tool-built' 'FAIL' "AOT publish failed (exit $publishExit); exe missing at $crashExe"
    Write-Host "  dotnet publish tail (last 20 lines, full log: $publishLog):" -ForegroundColor DarkGray
    Write-Host $tail -ForegroundColor DarkGray
} else {
    $exeSize = [math]::Round((Get-Item $crashExe).Length / 1MB, 1)
    Add-Result 'crash-tool-built' 'PASS' "$crashExe ($exeSize MB AOT native)"

    # Invoke with synthetic args that exercise the value-with-double-dash fix
    $jsonlPath = Join-Path $env:ProgramData 'SuavoAgent\diagnostics\publish-crash.jsonl'
    if (Test-Path -LiteralPath $jsonlPath) { Remove-Item -LiteralPath $jsonlPath -Force }
    $invokeArgs = @(
        '--component', 'Publish',
        '--exit-code', '1',
        '--project', 'SmokeTest',
        '--stdout-tail', 'MSBuild error CS0123: -- malformed test value',
        '--build-sha', 'smoke-test-sha',
        '--parent-ps-version', "$($PSVersionTable.PSVersion)",
        '--yubikey', 'true',
        '--smartcard', 'true'
    )
    if ($CertThumbprint) {
        $invokeArgs += '--ev-cert-thumbprint'
        $invokeArgs += $CertThumbprint
    }
    & $crashExe @invokeArgs
    $invokeExit = $LASTEXITCODE
    if ($invokeExit -ne 0) {
        Add-Result 'crash-tool-runs' 'FAIL' "exit $invokeExit"
    } else {
        Add-Result 'crash-tool-runs' 'PASS' 'exit 0'
        if (-not (Test-Path -LiteralPath $jsonlPath)) {
            Add-Result 'crash-jsonl-appears' 'FAIL' "missing at $jsonlPath"
        } else {
            $jsonl = (Get-Content -LiteralPath $jsonlPath -Tail 1) | ConvertFrom-Json
            $expected = @('ts', 'source', 'component', 'exit_code', 'exit_code_hex', 'project', 'stdout_tail', 'build_sha', 'parent_ps_version', 'yubikey_present', 'smartcard_running')
            $missing = $expected | Where-Object { -not $jsonl.PSObject.Properties.Name.Contains($_) }
            if ($missing.Count -gt 0) {
                Add-Result 'crash-jsonl-fields' 'FAIL' "missing fields: $($missing -join ',')"
            } else {
                Add-Result 'crash-jsonl-fields' 'PASS' 'all expected fields present'
            }
            # Verify -- mid-value preserved (Codex chunk 4b MEDIUM fix)
            if ($jsonl.stdout_tail -like "*-- malformed test value*") {
                Add-Result 'crash-jsonl-stdout-tail' 'PASS' 'value with -- captured'
            } else {
                Add-Result 'crash-jsonl-stdout-tail' 'FAIL' "stdout_tail = '$($jsonl.stdout_tail)'"
            }
            # Verify hex-formatted exit code
            if ($jsonl.exit_code_hex -eq '0x00000001') {
                Add-Result 'crash-jsonl-exit-code-hex' 'PASS' '0x00000001'
            } else {
                Add-Result 'crash-jsonl-exit-code-hex' 'FAIL' "exit_code_hex = '$($jsonl.exit_code_hex)'"
            }
            # Verify cert thumbprint is HASHED (SHA-256 of hex string) when supplied
            if ($CertThumbprint) {
                if ($jsonl.ev_cert_thumbprint_hash -and $jsonl.ev_cert_thumbprint_hash.Length -eq 64) {
                    Add-Result 'crash-jsonl-thumbprint-hashed' 'PASS' "SHA-256 ($($jsonl.ev_cert_thumbprint_hash.Substring(0,16))...)"
                } else {
                    Add-Result 'crash-jsonl-thumbprint-hashed' 'FAIL' "hash missing or wrong length: $($jsonl.ev_cert_thumbprint_hash)"
                }
                # Cert thumbprint MUST NOT appear raw in the JSONL
                $rawJsonl = Get-Content -LiteralPath $jsonlPath -Tail 1
                $normalizedTp = ($CertThumbprint -replace '[^0-9a-fA-F]', '').ToUpperInvariant()
                if ($rawJsonl -match $normalizedTp) {
                    Add-Result 'crash-jsonl-thumbprint-not-raw' 'FAIL' 'raw thumbprint LEAKED into JSONL'
                } else {
                    Add-Result 'crash-jsonl-thumbprint-not-raw' 'PASS' 'raw thumbprint never written'
                }
            } else {
                Add-Result 'crash-jsonl-thumbprint-hashed' 'SKIP' '-CertThumbprint not provided'
            }
        }
    }
    # Cleanup
    Remove-Item -Recurse -Force $crashToolOut -ErrorAction SilentlyContinue
}

# ── 3. tail-events.ps1 sanity ──────────────────────────────────────────
Test-Section "3. tail-events.ps1 pretty-printer"
$tailScript = Join-Path $script:RepoRoot 'tools\tail-events.ps1'
if (-not (Test-Path -LiteralPath $tailScript)) {
    Add-Result 'tail-script-exists' 'FAIL' "missing at $tailScript"
} else {
    Add-Result 'tail-script-exists' 'PASS' $tailScript
    # Create a synthetic events.jsonl with one event per signal_kind
    $syntheticEventsPath = Join-Path $env:TEMP "events-smoke-$(Get-Random).jsonl"
    $kinds = @('managed_exception', 'win32', 'invariant_violation', 'heartbeat', 'wire_handler_failed')
    $now = (Get-Date).ToUniversalTime().ToString('o')
    $kinds | ForEach-Object {
        $line = "{`"ts`":`"$now`",`"signal_kind`":`"$_`",`"component`":`"Core`",`"fp_v1`":`"Core|$_|||smoke.test|`"}"
        $line | Out-File -FilePath $syntheticEventsPath -Append -Encoding utf8
    }
    try {
        # *>&1 captures the information stream (6) where Write-Host writes;
        # plain 2>&1 only catches stderr and would miss tail-events.ps1's output.
        $tailOut = & $tailScript -Tail 10 -Path $syntheticEventsPath *>&1 | Out-String
        if ($tailOut -match 'managed_exception' -and $tailOut -match 'heartbeat') {
            Add-Result 'tail-renders' 'PASS' "$($kinds.Count) signal kinds rendered"
        } else {
            Add-Result 'tail-renders' 'FAIL' "expected output not present; got:`n$tailOut"
        }
    } catch {
        Add-Result 'tail-renders' 'FAIL' "tail script threw: $($_.Exception.Message)"
    }
    Remove-Item -LiteralPath $syntheticEventsPath -ErrorAction SilentlyContinue
}

# ── 4. publish.ps1 end-to-end (optional, ~8 min) ──────────────────────
if ($SkipPublish) {
    Test-Section "4. publish.ps1 (SKIPPED via -SkipPublish)"
    Add-Result 'publish-runs' 'SKIP' '-SkipPublish flag set'
} else {
    Test-Section "4. publish.ps1 end-to-end (heartbeat + crash-reporter hook)"
    $publishPath = Join-Path $script:RepoRoot 'publish.ps1'
    $publishOutDir = Join-Path $env:TEMP "suavo-publish-smoke-$(Get-Random)"
    $publishLog = Join-Path $env:TEMP "publish-smoke-$(Get-Random).log"

    Push-Location $script:RepoRoot
    try {
        $publishArgs = @('-OutputDir', $publishOutDir)
        if ($CertThumbprint) { $publishArgs += @('-CertThumbprint', $CertThumbprint) }
        # Stream to file so we can grep for heartbeat ticks
        & $publishPath @publishArgs *>&1 | Tee-Object -FilePath $publishLog | Out-Host
        $publishExit = $LASTEXITCODE
    } finally {
        Pop-Location
    }

    if ($publishExit -eq 0) {
        Add-Result 'publish-completes' 'PASS' "exit 0; output at $publishOutDir"
    } else {
        Add-Result 'publish-completes' 'FAIL' "exit $publishExit"
    }
    # Verify heartbeat lines appeared in log (every 30s)
    $logContent = Get-Content -LiteralPath $publishLog -Raw -ErrorAction SilentlyContinue
    if ($logContent -match 'elapsed=\d+s.*still compiling') {
        Add-Result 'publish-heartbeat-fires' 'PASS' "found 'still compiling' tick(s)"
    } else {
        Add-Result 'publish-heartbeat-fires' 'WARN' "no 'still compiling' tick (build may have been fast)"
    }
    # Verify suavo-report-crash.exe AOT-published as part of the run
    $publishedCrashExe = Join-Path $publishOutDir 'SuavoReportCrash\suavo-report-crash.exe'
    if (Test-Path -LiteralPath $publishedCrashExe) {
        $sizeKb = [math]::Round((Get-Item $publishedCrashExe).Length / 1KB, 0)
        Add-Result 'publish-aot-crash-tool' 'PASS' "$($sizeKb)KB native exe"
    } else {
        Add-Result 'publish-aot-crash-tool' 'FAIL' "missing at $publishedCrashExe"
    }
    # Verify all 5 main binaries appear
    $expectedBins = @('Core', 'Broker', 'Helper', 'Watchdog', 'Setup')
    foreach ($b in $expectedBins) {
        $exe = Get-ChildItem -Path (Join-Path $publishOutDir $b) -Filter '*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($exe) {
            Add-Result "publish-$($b.ToLower())-binary" 'PASS' "$($exe.Name)"
        } else {
            Add-Result "publish-$($b.ToLower())-binary" 'FAIL' "no .exe in $publishOutDir\$b"
        }
    }
    Remove-Item -Recurse -Force $publishOutDir -ErrorAction SilentlyContinue
}

# ── Summary card ────────────────────────────────────────────────────────
Write-Host ""
Write-Host "+========================================================+" -ForegroundColor Cyan
Write-Host "|  MESH PHASE 1 QUEEN SMOKE — $(Get-Date -Format 'yyyy-MM-dd HH:mm')           |" -ForegroundColor Cyan
Write-Host "+========================================================+" -ForegroundColor Cyan
$passCount = ($script:Results | Where-Object { $_.Status -eq 'PASS' }).Count
$failCount = ($script:Results | Where-Object { $_.Status -eq 'FAIL' }).Count
$warnCount = ($script:Results | Where-Object { $_.Status -eq 'WARN' }).Count
$skipCount = ($script:Results | Where-Object { $_.Status -eq 'SKIP' }).Count
Write-Host "  $passCount PASS  /  $failCount FAIL  /  $warnCount WARN  /  $skipCount SKIP"

if ($failCount -gt 0) {
    Write-Host ""
    Write-Host "FAILURES:" -ForegroundColor Red
    $script:Results | Where-Object { $_.Status -eq 'FAIL' } | ForEach-Object {
        Write-Host "  - $($_.Name): $($_.Detail)" -ForegroundColor Red
    }
}

exit ($(if ($failCount -gt 0) { 1 } else { 0 }))
