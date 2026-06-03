#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }
<#
.SYNOPSIS
    Pester tests for scripts/Test-QueenShipPreflight.ps1

.DESCRIPTION
    Each Test-* check function is verified in isolation with mocked
    inputs (registry/cert/service/env queries). Asserts:
      - PASS/FAIL/WARN/SKIP status correctness
      - Actionable single-line "fix" text on every FAIL/WARN
      - ASCII summary card layout is consistent

    Run on Mac dev box with `pwsh -c "Invoke-Pester ./tests/PreflightTests"`
    or on Queen via remote-pwsh.
#>

BeforeAll {
    $script:PreflightScript = Join-Path $PSScriptRoot ".." ".." "scripts" "Test-QueenShipPreflight.ps1"
    . $script:PreflightScript
}

Describe "Test-PowerShell7Version" {
    It "PASS on PS 7.0+" {
        $r = Test-PowerShell7Version -PsVersion ([Version]"7.0.0")
        $r.status | Should -Be "PASS"
        $r.detail | Should -Match "PowerShell 7"
    }
    It "PASS on PS 7.6.0" {
        $r = Test-PowerShell7Version -PsVersion ([Version]"7.6.0")
        $r.status | Should -Be "PASS"
    }
    It "FAIL on PS 5.1 with actionable fix" {
        $r = Test-PowerShell7Version -PsVersion ([Version]"5.1.19041")
        $r.status | Should -Be "FAIL"
        $r.fix | Should -Match "winget install Microsoft.PowerShell"
    }
    It "FAIL on PS 6.x" {
        $r = Test-PowerShell7Version -PsVersion ([Version]"6.2.4")
        $r.status | Should -Be "FAIL"
    }
}

Describe "Test-PwshOnPath" {
    It "PASS when pwsh is resolvable" {
        $r = Test-PwshOnPath -Resolver { [pscustomobject]@{ Source = "C:\Program Files\PowerShell\7\pwsh.exe" } }
        $r.status | Should -Be "PASS"
        $r.detail | Should -Match "pwsh.exe"
    }
    It "FAIL when pwsh missing with actionable fix" {
        $r = Test-PwshOnPath -Resolver { $null }
        $r.status | Should -Be "FAIL"
        $r.fix | Should -Match "winget install Microsoft.PowerShell"
    }
}

Describe "Test-DotnetSdk" {
    It "PASS on .NET 8.0.404" {
        $r = Test-DotnetSdk -Version "8.0.404"
        $r.status | Should -Be "PASS"
        $r.detail | Should -Match "8.0.404"
    }
    It "PASS on .NET 9.x" {
        $r = Test-DotnetSdk -Version "9.0.100"
        $r.status | Should -Be "PASS"
    }
    It "FAIL on .NET 6 (too old) with actionable fix" {
        $r = Test-DotnetSdk -Version "6.0.420"
        $r.status | Should -Be "FAIL"
        $r.fix | Should -Match "Microsoft.DotNet.SDK.8"
    }
    It "FAIL when dotnet not found" {
        $r = Test-DotnetSdk -Version ""
        $r.status | Should -Be "FAIL"
    }
    It "FAIL on malformed version string" {
        $r = Test-DotnetSdk -Version "not-a-version"
        $r.status | Should -Be "FAIL"
    }
}

Describe "Test-EvCertThumbprint" {
    It "SKIP when -SkipSigning" {
        $r = Test-EvCertThumbprint -Skip $true -Thumbprint ""
        $r.status | Should -Be "SKIP"
    }
    It "FAIL when thumbprint empty (not skipping) with actionable fix" {
        $r = Test-EvCertThumbprint -Skip $false -Thumbprint ""
        $r.status | Should -Be "FAIL"
        $r.fix | Should -Match "SUAVO_CERT_THUMBPRINT"
    }
    It "PASS when thumbprint resolves to cert (Windows only)" {
        if (-not (Test-Path Cert:\CurrentUser\My -ErrorAction SilentlyContinue)) {
            Set-ItResult -Skipped -Because "Cert: drive unavailable on non-Windows"
            return
        }
        $fake = [pscustomobject]@{
            Thumbprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF01"
            NotAfter   = (Get-Date).AddYears(1)
        }
        $r = Test-EvCertThumbprint -Skip $false -Thumbprint "ABCDEF0123456789ABCDEF0123456789ABCDEF01" `
            -Resolver { param($Tp) $fake }
        $r.status | Should -Be "PASS"
        $r.detail | Should -Match "thumbprint matched"
    }
    It "FAIL when thumbprint does not resolve (Windows only)" {
        if (-not (Test-Path Cert:\CurrentUser\My -ErrorAction SilentlyContinue)) {
            Set-ItResult -Skipped -Because "Cert: drive unavailable on non-Windows"
            return
        }
        $r = Test-EvCertThumbprint -Skip $false -Thumbprint "DEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEF" `
            -Resolver { param($Tp) @() }
        $r.status | Should -Be "FAIL"
        $r.fix | Should -Match "matches an installed cert"
    }
    It "SKIP on non-Windows when thumbprint is set" {
        if (Test-Path Cert:\CurrentUser\My -ErrorAction SilentlyContinue) {
            Set-ItResult -Skipped -Because "Cert: drive available — non-Windows path not exercised"
            return
        }
        $r = Test-EvCertThumbprint -Skip $false -Thumbprint "ABCDEF0123456789ABCDEF0123456789ABCDEF01"
        $r.status | Should -Be "SKIP"
        $r.detail | Should -Match "non-Windows"
    }
}

Describe "Test-SentryDsn" {
    It "PASS when env var set" {
        $r = Test-SentryDsn -EnvDsn "https://abc@o123.ingest.sentry.io/456"
        $r.status | Should -Be "PASS"
        $r.detail | Should -Match "env"
    }
    It "PASS when appsettings.json has Diagnostics:Dsn" {
        $tmp = New-TemporaryFile
        try {
            @{ Diagnostics = @{ Dsn = "https://abc@o123.ingest.sentry.io/456" } } |
                ConvertTo-Json | Set-Content -Path $tmp
            $r = Test-SentryDsn -EnvDsn "" -AppsettingsPath $tmp
            $r.status | Should -Be "PASS"
            $r.detail | Should -Match "appsettings"
        } finally {
            Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        }
    }
    It "WARN when neither env nor appsettings has DSN (with fix)" {
        $r = Test-SentryDsn -EnvDsn "" -AppsettingsPath "/nonexistent/appsettings.json"
        $r.status | Should -Be "WARN"
        $r.fix | Should -Match "SUAVO_SENTRY_DSN"
    }
    It "WARN when appsettings.json is malformed JSON" {
        $tmp = New-TemporaryFile
        try {
            Set-Content -Path $tmp -Value "{ this is not json"
            $r = Test-SentryDsn -EnvDsn "" -AppsettingsPath $tmp
            $r.status | Should -Be "WARN"
        } finally {
            Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        }
    }
}

Describe "Test-GitTreeClean" {
    It "PASS when no uncommitted changes" {
        $r = Test-GitTreeClean -Resolver { "" }
        $r.status | Should -Be "PASS"
    }
    It "FAIL when uncommitted changes with actionable fix" {
        $r = Test-GitTreeClean -Resolver { " M src/foo.cs`n M src/bar.cs" }
        $r.status | Should -Be "FAIL"
        $r.fix | Should -Match "Commit or stash"
    }
    It "WARN (not FAIL) when -AllowDirtyTree" {
        $r = Test-GitTreeClean -Allow $true -Resolver { " M src/foo.cs" }
        $r.status | Should -Be "WARN"
    }
}

Describe "Test-BuildCacheFresh" {
    It "PASS when no stale dirs" {
        $tmp = New-Item -ItemType Directory -Path (Join-Path ([System.IO.Path]::GetTempPath()) "preflight-test-fresh-$(New-Guid)") -Force
        try {
            $r = Test-BuildCacheFresh -MaxAgeDays 1 -CacheRoots @($tmp.FullName)
            $r.status | Should -Be "PASS"
        } finally {
            Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    It "WARN when stale bin/obj dirs detected" {
        $tmp = New-Item -ItemType Directory -Path (Join-Path ([System.IO.Path]::GetTempPath()) "preflight-test-stale-$(New-Guid)") -Force
        try {
            $bin = New-Item -ItemType Directory -Path (Join-Path $tmp "bin") -Force
            $oldDate = (Get-Date).AddDays(-30)
            (Get-Item $bin.FullName).LastWriteTime = $oldDate
            $r = Test-BuildCacheFresh -MaxAgeDays 1 -CacheRoots @($tmp.FullName)
            $r.status | Should -Be "WARN"
            $r.fix | Should -Match "dotnet clean"
        } finally {
            Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Describe "Format-SummaryCard" {
    It "renders a bordered card with all sections" {
        $results = [System.Collections.Generic.List[object]]::new()
        $results.Add((New-PreflightResult -Name "x" -Status PASS -Detail "everything fine"))
        $results.Add((New-PreflightResult -Name "y" -Status FAIL -Detail "broken" -Fix "do the thing"))
        $card = Format-SummaryCard -Results $results
        $card | Should -Match "QUEEN SHIP PREFLIGHT"
        $card | Should -Match "\[PASS\]"
        $card | Should -Match "\[FAIL\]"
        $card | Should -Match "-> fix: do the thing"
        $card | Should -Match "RESULT: 1 FAIL"
    }
    It "every FAIL row is followed by a fix row" {
        $results = [System.Collections.Generic.List[object]]::new()
        $results.Add((New-PreflightResult -Name "a" -Status FAIL -Detail "d1" -Fix "f1"))
        $results.Add((New-PreflightResult -Name "b" -Status FAIL -Detail "d2" -Fix "f2"))
        $card = Format-SummaryCard -Results $results
        ([regex]::Matches($card, "-> fix:")).Count | Should -Be 2
    }
    It "truncates rows longer than 56 chars" {
        $results = [System.Collections.Generic.List[object]]::new()
        $long = "x" * 200
        $results.Add((New-PreflightResult -Name "x" -Status PASS -Detail $long))
        $card = Format-SummaryCard -Results $results
        $card -split "`n" | ForEach-Object {
            if ($_ -match '^\|') {
                $_.Length | Should -BeLessOrEqual 62  # | + space + 56 + space + | + slack
            }
        }
    }
}

Describe "New-PreflightResult" {
    It "rejects unknown status values" {
        { New-PreflightResult -Name "x" -Status "BOGUS" } | Should -Throw
    }
    It "accepts PASS/FAIL/WARN/SKIP" {
        foreach ($s in @("PASS", "FAIL", "WARN", "SKIP")) {
            $r = New-PreflightResult -Name "x" -Status $s
            $r.status | Should -Be $s
        }
    }
}
