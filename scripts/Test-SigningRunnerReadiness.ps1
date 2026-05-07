#Requires -Version 5.1
<#
.SYNOPSIS
    Checks whether a Windows machine is ready to sign SuavoAgent field releases.
.DESCRIPTION
    This is a no-PHI release-gate probe for the self-hosted Windows runner.
    It verifies the EV code-signing certificate and private key are visible to
    the current user, signtool is installed, the GitHub Actions runner service
    exists and is running, and the local build prerequisites are present.
#>

[CmdletBinding()]
param(
    [string]$ExpectedSubjectPattern = "MKM TECHNOLOGIES LLC|Suavo",
    [string]$ExpectedThumbprint,
    [switch]$Json
)

$ErrorActionPreference = "Stop"

$results = New-Object System.Collections.Generic.List[object]

function Add-ReadinessResult {
    param(
        [string]$Name,
        [bool]$Ok,
        [string]$Detail,
        [hashtable]$Metadata = @{}
    )

    $results.Add([pscustomobject]@{
        name = $Name
        ok = $Ok
        detail = $Detail
        metadata = $Metadata
    })
}

function Normalize-Thumbprint {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    return ($Value -replace "[^0-9a-fA-F]", "").ToUpperInvariant()
}

function Get-CodeSigningCertificateCandidates {
    $stores = @(
        "Cert:\CurrentUser\My",
        "Cert:\LocalMachine\My"
    )

    foreach ($store in $stores) {
        $certificates = @(Get-ChildItem -Path $store -ErrorAction SilentlyContinue)
        foreach ($certificate in $certificates) {
            $ekuNames = @($certificate.EnhancedKeyUsageList | ForEach-Object { $_.FriendlyName })
            $ekuOids = @($certificate.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId })
            $isCodeSigning =
                ($ekuNames -contains "Code Signing") -or
                ($ekuOids -contains "1.3.6.1.5.5.7.3.3")

            if ($isCodeSigning -or $certificate.Subject -match $ExpectedSubjectPattern) {
                [pscustomobject]@{
                    Store = $store
                    Subject = $certificate.Subject
                    Thumbprint = $certificate.Thumbprint
                    NotAfter = $certificate.NotAfter
                    IsCodeSigning = $isCodeSigning
                    HasPrivateKey = $certificate.HasPrivateKey
                }
            }
        }
    }
}

function Find-SignTool {
    $roots = New-Object System.Collections.Generic.List[string]
    if (${env:ProgramFiles(x86)}) {
        $roots.Add((Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"))
    }
    if ($env:ProgramFiles) {
        $roots.Add((Join-Path $env:ProgramFiles "Windows Kits\10\bin"))
    }

    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            continue
        }

        $sdkDirectories = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending
        foreach ($directory in $sdkDirectories) {
            $candidate = Join-Path $directory.FullName "x64\signtool.exe"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return $candidate
            }
        }
    }

    return $null
}

function Invoke-VersionCommand {
    param(
        [string]$Name,
        [string]$Command,
        [string[]]$Arguments,
        [string]$ExpectedPrefix
    )

    try {
        $output = & $Command @Arguments 2>$null
        $exitCode = $LASTEXITCODE
        $text = (@($output) -join " ").Trim()
        $ok = $exitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($text)
        if ($ExpectedPrefix) {
            $ok = $ok -and $text.StartsWith($ExpectedPrefix)
        }

        Add-ReadinessResult `
            -Name $Name `
            -Ok $ok `
            -Detail $(if ($ok) { $text } else { "unexpected_version" }) `
            -Metadata @{ expectedPrefix = $ExpectedPrefix }
    } catch {
        Add-ReadinessResult -Name $Name -Ok $false -Detail "missing"
    }
}

$expectedThumbprintNormalized = Normalize-Thumbprint $ExpectedThumbprint
$certCandidates = @(Get-CodeSigningCertificateCandidates)
$usableCerts = @(
    $certCandidates | Where-Object {
        $_.IsCodeSigning -and
        $_.HasPrivateKey -and
        $_.Subject -match $ExpectedSubjectPattern -and
        (
            -not $expectedThumbprintNormalized -or
            (Normalize-Thumbprint $_.Thumbprint) -eq $expectedThumbprintNormalized
        )
    }
)

Add-ReadinessResult `
    -Name "signing-cert" `
    -Ok ($usableCerts.Count -gt 0) `
    -Detail $(if ($usableCerts.Count -gt 0) { "present" } else { "missing_expected_code_signing_cert" }) `
    -Metadata @{
        expectedSubjectPattern = $ExpectedSubjectPattern
        expectedThumbprintConfigured = -not [string]::IsNullOrWhiteSpace($ExpectedThumbprint)
        candidateCount = $certCandidates.Count
        matchingCount = $usableCerts.Count
        matches = @($usableCerts | ForEach-Object {
            @{
                store = $_.Store
                subject = $_.Subject
                thumbprint = $_.Thumbprint
                notAfter = $_.NotAfter.ToString("o")
                hasPrivateKey = $_.HasPrivateKey
            }
        })
    }

$signtool = Find-SignTool
Add-ReadinessResult `
    -Name "signtool" `
    -Ok (-not [string]::IsNullOrWhiteSpace($signtool)) `
    -Detail $(if ($signtool) { "present" } else { "missing_windows_sdk_signtool" }) `
    -Metadata @{ path = $signtool }

Invoke-VersionCommand -Name "dotnet-sdk" -Command "dotnet" -Arguments @("--version") -ExpectedPrefix "8."
Invoke-VersionCommand -Name "git" -Command "git" -Arguments @("--version") -ExpectedPrefix "git version"

$runnerServices = @(Get-Service -Name "actions.runner*" -ErrorAction SilentlyContinue)
$runningRunnerServices = @($runnerServices | Where-Object { $_.Status -eq "Running" })
Add-ReadinessResult `
    -Name "actions-runner-service" `
    -Ok ($runningRunnerServices.Count -gt 0) `
    -Detail $(if ($runningRunnerServices.Count -gt 0) { "running" } elseif ($runnerServices.Count -gt 0) { "installed_not_running" } else { "missing" }) `
    -Metadata @{
        serviceCount = $runnerServices.Count
        services = @($runnerServices | ForEach-Object {
            @{
                name = $_.Name
                status = $_.Status.ToString()
                displayName = $_.DisplayName
            }
        })
    }

$failed = @($results | Where-Object { -not $_.ok })
$summary = [pscustomobject]@{
    ok = $failed.Count -eq 0
    checkedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    failedCount = $failed.Count
    results = @($results)
}

if ($Json) {
    $summary | ConvertTo-Json -Depth 8
} else {
    Write-Host ""
    Write-Host "SuavoAgent signing runner readiness"
    Write-Host "-----------------------------------"
    foreach ($result in $results) {
        $status = if ($result.ok) { "PASS" } else { "FAIL" }
        Write-Host ("{0,-5} {1,-28} {2}" -f $status, $result.name, $result.detail)
    }
    Write-Host ""
}

if (-not $summary.ok) {
    exit 1
}
