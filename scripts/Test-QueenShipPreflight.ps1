#Requires -Version 5.1
<#
.SYNOPSIS
    Queen ship-preflight: 9 environment checks that must pass before publish.ps1
    will produce a release-ready SuavoAgent build.

.DESCRIPTION
    Implements PR 1 of the Diagnostic Mesh Queen-first spec
    (docs/architecture/diagnostic-mesh-queen-first.md §7 PR 1).

    Catches the entire class of "build fails because environment isn't ready"
    that burned 2 hours on 2026-05-12 (Bug 24 verification session). Runs
    as the first action of publish.ps1; aborts publish on any FAIL.

    Each check fails fast with a single-line actionable error. Output is a
    green/red ASCII summary card by default, or structured JSON with -Json.

.PARAMETER SkipSigning
    Skip Yubikey / EV cert / SmartCard service checks. Used when running
    publish.ps1 without -CertThumbprint (unsigned dev builds).

.PARAMETER CertThumbprint
    EV code-signing certificate thumbprint (SHA1). Falls back to
    $env:SUAVO_CERT_THUMBPRINT. Empty + -SkipSigning = signing skipped.

.PARAMETER Json
    Emit structured JSON instead of the ASCII summary card.

.PARAMETER AllowDirtyTree
    Allow uncommitted changes in src/ to pass the git-tree check (still WARN).
    Default behavior is FAIL on uncommitted changes.

.PARAMETER CleanCacheMaxAgeDays
    Build cache (obj/ bin/) age in days beyond which we WARN. Default 1.

.OUTPUTS
    Exit 0 on all-PASS (WARN does not fail).
    Exit 1 on any FAIL.
#>

[CmdletBinding()]
param(
    [switch]$SkipSigning,
    [string]$CertThumbprint = $env:SUAVO_CERT_THUMBPRINT,
    [switch]$Json,
    [switch]$AllowDirtyTree,
    [int]$CleanCacheMaxAgeDays = 1
)

$ErrorActionPreference = "Stop"

# ── Result helpers ──────────────────────────────────────────────────────────

$script:Results = [System.Collections.Generic.List[object]]::new()

function New-PreflightResult {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet("PASS", "FAIL", "WARN", "SKIP")][string]$Status,
        [string]$Detail = "",
        [string]$Fix = "",
        [hashtable]$Metadata = @{}
    )
    [pscustomobject]@{
        name     = $Name
        status   = $Status
        detail   = $Detail
        fix      = $Fix
        metadata = $Metadata
    }
}

function Add-PreflightResult {
    param([Parameter(Mandatory)][pscustomobject]$Result)
    $script:Results.Add($Result)
}

# ── Individual check functions (each testable in isolation) ─────────────────

function Test-PowerShell7Version {
    # Untyped param — pwsh 7's $PSVersionTable.PSVersion is [SemanticVersion],
    # not [System.Version]. The implicit conversion can throw via reflection.
    param($PsVersion = $PSVersionTable.PSVersion)
    if ($PsVersion.Major -ge 7) {
        return New-PreflightResult -Name "pwsh-version" -Status PASS `
            -Detail ("PowerShell {0}" -f $PsVersion)
    }
    return New-PreflightResult -Name "pwsh-version" -Status FAIL `
        -Detail ("PowerShell {0} too old (need 7+)" -f $PsVersion) `
        -Fix "Install pwsh: winget install Microsoft.PowerShell. Then re-run via pwsh.exe."
}

function Test-PwshOnPath {
    param([scriptblock]$Resolver = { Get-Command pwsh -ErrorAction SilentlyContinue })
    $cmd = & $Resolver
    if ($cmd) {
        return New-PreflightResult -Name "pwsh-on-path" -Status PASS `
            -Detail $cmd.Source
    }
    return New-PreflightResult -Name "pwsh-on-path" -Status FAIL `
        -Detail "pwsh.exe not on PATH" `
        -Fix "Install pwsh: winget install Microsoft.PowerShell"
}

function Test-DotnetSdk {
    param([string]$Version)
    if (-not $PSBoundParameters.ContainsKey('Version')) {
        try {
            $Version = (& dotnet --version 2>$null).Trim()
        } catch {
            $Version = $null
        }
    }
    if (-not $Version) {
        return New-PreflightResult -Name "dotnet-sdk" -Status FAIL `
            -Detail "dotnet SDK not found" `
            -Fix "Install: winget install Microsoft.DotNet.SDK.8"
    }
    if ($Version -match '^(\d+)\.') {
        $major = [int]$Matches[1]
        if ($major -ge 8) {
            return New-PreflightResult -Name "dotnet-sdk" -Status PASS `
                -Detail ".NET SDK $Version"
        }
    }
    return New-PreflightResult -Name "dotnet-sdk" -Status FAIL `
        -Detail ".NET SDK $Version too old (need 8+)" `
        -Fix "Install: winget install Microsoft.DotNet.SDK.8"
}

function Test-YubikeyPresent {
    param(
        [bool]$Skip = $false,
        [scriptblock]$Resolver = {
            if (Get-Command Get-PnpDevice -ErrorAction SilentlyContinue) {
                Get-PnpDevice -ErrorAction SilentlyContinue |
                    Where-Object { $_.FriendlyName -match 'Yubikey|YubiKey' -and $_.Status -eq 'OK' }
            }
        }
    )
    if ($Skip) {
        return New-PreflightResult -Name "yubikey" -Status SKIP `
            -Detail "skipped (-SkipSigning)"
    }
    if (-not (Get-Command Get-PnpDevice -ErrorAction SilentlyContinue)) {
        return New-PreflightResult -Name "yubikey" -Status SKIP `
            -Detail "Get-PnpDevice unavailable (non-Windows)"
    }
    $device = & $Resolver | Select-Object -First 1
    if ($device) {
        return New-PreflightResult -Name "yubikey" -Status PASS `
            -Detail ("present ({0})" -f $device.FriendlyName)
    }
    return New-PreflightResult -Name "yubikey" -Status FAIL `
        -Detail "Yubikey reader not detected" `
        -Fix "Insert Yubikey, or run with -SkipSigning to publish unsigned."
}

function Test-EvCertThumbprint {
    param(
        [bool]$Skip = $false,
        [string]$Thumbprint,
        [scriptblock]$Resolver = {
            param($Tp)
            $normalized = ($Tp -replace '[^0-9a-fA-F]', '').ToUpperInvariant()
            Get-ChildItem -Path Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
                Where-Object {
                    (($_.Thumbprint -replace '[^0-9a-fA-F]', '').ToUpperInvariant()) -eq $normalized
                }
        }
    )
    if ($Skip) {
        return New-PreflightResult -Name "ev-cert" -Status SKIP `
            -Detail "skipped (-SkipSigning)"
    }
    if ([string]::IsNullOrWhiteSpace($Thumbprint)) {
        return New-PreflightResult -Name "ev-cert" -Status FAIL `
            -Detail '$env:SUAVO_CERT_THUMBPRINT not set' `
            -Fix 'Set $env:SUAVO_CERT_THUMBPRINT to the SHA1 of your code-signing cert, or run with -SkipSigning.'
    }
    if (-not (Test-Path Cert:\CurrentUser\My -ErrorAction SilentlyContinue)) {
        return New-PreflightResult -Name "ev-cert" -Status SKIP `
            -Detail "Cert:\ provider unavailable (non-Windows)"
    }
    $cert = & $Resolver $Thumbprint | Select-Object -First 1
    if ($cert) {
        return New-PreflightResult -Name "ev-cert" -Status PASS `
            -Detail ("thumbprint matched (expires {0:yyyy-MM-dd})" -f $cert.NotAfter)
    }
    return New-PreflightResult -Name "ev-cert" -Status FAIL `
        -Detail "thumbprint not found in CurrentUser\My store" `
        -Fix 'Verify $env:SUAVO_CERT_THUMBPRINT matches an installed cert, or run with -SkipSigning.'
}

function Test-SmartCardService {
    param(
        [bool]$Skip = $false,
        [scriptblock]$Resolver = { Get-Service -Name SCardSvr -ErrorAction SilentlyContinue }
    )
    if ($Skip) {
        return New-PreflightResult -Name "smartcard-svc" -Status SKIP `
            -Detail "skipped (-SkipSigning)"
    }
    if (-not (Get-Command Get-Service -ErrorAction SilentlyContinue)) {
        return New-PreflightResult -Name "smartcard-svc" -Status SKIP `
            -Detail "Get-Service unavailable (non-Windows)"
    }
    $svc = & $Resolver
    if (-not $svc) {
        return New-PreflightResult -Name "smartcard-svc" -Status FAIL `
            -Detail "SCardSvr service not installed" `
            -Fix "Reinstall Windows Smart Card components, or run with -SkipSigning."
    }
    if ($svc.Status -eq 'Running') {
        return New-PreflightResult -Name "smartcard-svc" -Status PASS `
            -Detail "running"
    }
    return New-PreflightResult -Name "smartcard-svc" -Status FAIL `
        -Detail ("status={0}" -f $svc.Status) `
        -Fix "Start-Service SCardSvr; or run with -SkipSigning."
}

function Test-SentryDsn {
    param(
        [string]$EnvDsn,
        [string]$AppsettingsPath = (Join-Path $PSScriptRoot ".." "src" "SuavoAgent.Core" "appsettings.json")
    )
    if (-not $PSBoundParameters.ContainsKey('EnvDsn')) {
        $EnvDsn = $env:SUAVO_SENTRY_DSN
    }
    if (-not [string]::IsNullOrWhiteSpace($EnvDsn)) {
        return New-PreflightResult -Name "sentry-dsn" -Status PASS `
            -Detail "present (env)"
    }
    if (Test-Path -LiteralPath $AppsettingsPath) {
        try {
            $cfg = Get-Content -Raw -LiteralPath $AppsettingsPath | ConvertFrom-Json -ErrorAction Stop
            if ($cfg.Diagnostics -and -not [string]::IsNullOrWhiteSpace($cfg.Diagnostics.Dsn)) {
                return New-PreflightResult -Name "sentry-dsn" -Status PASS `
                    -Detail "present (appsettings.json)"
            }
        } catch {
            # fall through to WARN
        }
    }
    return New-PreflightResult -Name "sentry-dsn" -Status WARN `
        -Detail "no Sentry DSN — mesh will ship in local-journal-only mode" `
        -Fix 'Set $env:SUAVO_SENTRY_DSN or add Diagnostics:Dsn to appsettings.json before Pilot 1.'
}

function Test-GitTreeClean {
    param(
        [bool]$Allow = $false,
        [scriptblock]$Resolver = { & git status --porcelain -- src/ 2>$null }
    )
    $status = & $Resolver
    if ([string]::IsNullOrWhiteSpace($status)) {
        return New-PreflightResult -Name "git-tree" -Status PASS -Detail "clean"
    }
    $count = ($status -split "`n" | Where-Object { $_.Trim() }).Count
    if ($Allow) {
        return New-PreflightResult -Name "git-tree" -Status WARN `
            -Detail ("$count uncommitted change(s) in src/ — allowed via -AllowDirtyTree") `
            -Fix "Commit or stash before publishing."
    }
    return New-PreflightResult -Name "git-tree" -Status FAIL `
        -Detail ("$count uncommitted change(s) in src/") `
        -Fix "Commit or stash before publishing, or re-run with -AllowDirtyTree."
}

function Test-BuildCacheFresh {
    param(
        [int]$MaxAgeDays,
        [string[]]$CacheRoots = @(
            (Join-Path $PSScriptRoot ".." "src"),
            (Join-Path $PSScriptRoot ".." "tests")
        )
    )
    if ($MaxAgeDays -le 0) { $MaxAgeDays = 1 }
    $cutoff = (Get-Date).AddDays(-$MaxAgeDays)

    $stale = @()
    foreach ($root in $CacheRoots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $dirs = Get-ChildItem -Path $root -Recurse -Directory -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -in @('bin', 'obj') }
        foreach ($d in $dirs) {
            if ($d.LastWriteTime -lt $cutoff) { $stale += $d.FullName }
        }
    }
    if ($stale.Count -eq 0) {
        return New-PreflightResult -Name "build-cache" -Status PASS -Detail "fresh"
    }
    return New-PreflightResult -Name "build-cache" -Status WARN `
        -Detail ("{0} stale bin/obj dir(s) older than {1}d" -f $stale.Count, $MaxAgeDays) `
        -Fix "Run with -CleanFirst or 'dotnet clean' to refresh."
}

# ── ASCII summary card renderer ─────────────────────────────────────────────

function Format-SummaryCard {
    param([System.Collections.Generic.List[object]]$Results)
    $width = 56
    $border = ('+' + ('=' * ($width + 2)) + '+')
    $blank  = ('|' + (' ' * ($width + 2)) + '|')

    $lines = @($border)
    $title = "QUEEN SHIP PREFLIGHT — $(Get-Date -Format 'yyyy-MM-dd')"
    $titlePadded = $title.PadLeft([math]::Floor(($width + $title.Length) / 2)).PadRight($width)
    $lines += ('| ' + $titlePadded + ' |')
    $lines += $border

    foreach ($r in $Results) {
        $statusTag = "[$($r.status)]".PadRight(7)
        $detail = if ($r.detail) { $r.detail } else { $r.name }
        $row = "$statusTag $detail"
        if ($row.Length -gt $width) { $row = $row.Substring(0, $width - 1) + '…' }
        $lines += ('| ' + $row.PadRight($width) + ' |')

        if ($r.status -in @('FAIL', 'WARN') -and $r.fix) {
            $fixRow = "  -> fix: $($r.fix)"
            if ($fixRow.Length -gt $width) { $fixRow = $fixRow.Substring(0, $width - 1) + '…' }
            $lines += ('| ' + $fixRow.PadRight($width) + ' |')
        }
    }

    $lines += $border
    $passCount = ($Results | Where-Object { $_.status -eq 'PASS' }).Count
    $failCount = ($Results | Where-Object { $_.status -eq 'FAIL' }).Count
    $warnCount = ($Results | Where-Object { $_.status -eq 'WARN' }).Count
    $skipCount = ($Results | Where-Object { $_.status -eq 'SKIP' }).Count
    $verdict = if ($failCount -gt 0) { "publish aborted." } else { "publish OK." }
    $resultLine = "RESULT: $failCount FAIL, $warnCount WARN, $passCount PASS, $skipCount SKIP — $verdict"
    if ($resultLine.Length -gt $width) { $resultLine = $resultLine.Substring(0, $width - 1) + '…' }
    $lines += ('| ' + $resultLine.PadRight($width) + ' |')
    $lines += $border
    $lines -join "`n"
}

# ── Entry point ─────────────────────────────────────────────────────────────

function Invoke-QueenShipPreflight {
    Add-PreflightResult (Test-PowerShell7Version)
    Add-PreflightResult (Test-PwshOnPath)
    Add-PreflightResult (Test-DotnetSdk)
    Add-PreflightResult (Test-YubikeyPresent -Skip:$SkipSigning)
    Add-PreflightResult (Test-EvCertThumbprint -Skip:$SkipSigning -Thumbprint $CertThumbprint)
    Add-PreflightResult (Test-SmartCardService -Skip:$SkipSigning)
    Add-PreflightResult (Test-SentryDsn)
    Add-PreflightResult (Test-GitTreeClean -Allow:$AllowDirtyTree)
    Add-PreflightResult (Test-BuildCacheFresh -MaxAgeDays $CleanCacheMaxAgeDays)
}

# Skip auto-invocation when dot-sourced for Pester tests.
if ($MyInvocation.InvocationName -ne '.') {
    Invoke-QueenShipPreflight

    $failed = @($script:Results | Where-Object { $_.status -eq 'FAIL' })

    if ($Json) {
        [pscustomobject]@{
            ok           = $failed.Count -eq 0
            checkedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
            failedCount  = $failed.Count
            results      = @($script:Results)
        } | ConvertTo-Json -Depth 6
    } else {
        Write-Host ""
        Write-Host (Format-SummaryCard -Results $script:Results)
        Write-Host ""
    }

    if ($failed.Count -gt 0) {
        exit 1
    }
}
