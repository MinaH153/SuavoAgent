#Requires -Version 5.1
<#
.SYNOPSIS
    No-PHI SuavoAgent release and installed-machine smoke probe.
.DESCRIPTION
    ReleaseArtifact mode validates a staged release directory or zip before a
    GitHub release is published. Installed mode validates the local Windows
    install: services, binaries, bootstrap repair path, and heartbeat readiness
    prerequisites. The probe never prints appsettings values or log bodies.
#>

[CmdletBinding()]
param(
    [ValidateSet("ReleaseArtifact", "Installed")]
    [string]$Mode = "Installed",

    [string]$ReleaseZip,
    [string]$ReleaseDir = ".\release",
    [string]$InstallDir = "C:\Program Files\Suavo\Agent",
    [string]$ProgramDataDir = "$env:ProgramData\SuavoAgent",
    [string]$BootstrapPath,
    [switch]$Json
)

$ErrorActionPreference = "Stop"

$requiredBinaries = @(
    "SuavoAgent.Core.exe",
    "SuavoAgent.Broker.exe",
    "SuavoAgent.Helper.exe",
    "SuavoAgent.Watchdog.exe",
    "SuavoSetup.exe"
)

$requiredServices = @(
    "SuavoAgent.Core",
    "SuavoAgent.Broker",
    "SuavoAgent.Watchdog"
)

$results = New-Object System.Collections.Generic.List[object]

function Add-ProbeResult {
    param(
        [string]$Name,
        [bool]$Ok,
        [string]$Detail = "",
        [hashtable]$Metadata = @{}
    )

    $results.Add([pscustomobject]@{
        name = $Name
        ok = $Ok
        detail = $Detail
        metadata = $Metadata
    })
}

function Get-HashPrefix {
    param([string]$Path)
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    return $hash.Substring(0, 16)
}

function Test-ReleaseBinaries {
    param([string]$Directory)

    foreach ($binary in $requiredBinaries) {
        $path = Join-Path $Directory $binary
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Add-ProbeResult -Name "binary:$binary" -Ok $false -Detail "missing"
            continue
        }

        $item = Get-Item -LiteralPath $path
        if ($item.Length -le 0) {
            Add-ProbeResult -Name "binary:$binary" -Ok $false -Detail "empty"
            continue
        }

        Add-ProbeResult `
            -Name "binary:$binary" `
            -Ok $true `
            -Detail "present" `
            -Metadata @{ bytes = $item.Length; sha256Prefix = Get-HashPrefix $path }
    }
}

function Test-BootstrapRepairPath {
    param([string]$Path)

    if (-not $Path) {
        Add-ProbeResult -Name "bootstrap:repair-path" -Ok $false -Detail "path_missing"
        return
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-ProbeResult -Name "bootstrap:repair-path" -Ok $false -Detail "file_missing"
        return
    }

    $source = Get-Content -LiteralPath $Path -Raw
    $null = [scriptblock]::Create($source)
    $parseErrors = $null
    [System.Management.Automation.PSParser]::Tokenize($source, [ref]$parseErrors) | Out-Null
    if ($parseErrors -and $parseErrors.Count -gt 0) {
        Add-ProbeResult -Name "bootstrap:repair-path" -Ok $false -Detail "parse_error"
        return
    }

    $hasRepairSwitch = $source.Contains("[switch]`$Repair")
    $hasRepairInvocation = $source.Contains("--repair") -or $source.Contains("-Repair")
    Add-ProbeResult `
        -Name "bootstrap:repair-path" `
        -Ok ($hasRepairSwitch -and $hasRepairInvocation) `
        -Detail "parseable" `
        -Metadata @{ repairSwitch = $hasRepairSwitch; repairInvocation = $hasRepairInvocation }
}

function Test-InstalledServices {
    foreach ($serviceName in $requiredServices) {
        $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if (-not $svc) {
            Add-ProbeResult -Name "service:$serviceName" -Ok $false -Detail "missing"
            continue
        }

        Add-ProbeResult `
            -Name "service:$serviceName" `
            -Ok ($svc.Status -eq "Running") `
            -Detail ([string]$svc.Status)
    }
}

function Test-CrashLogMarkers {
    param([string]$DataDirectory)

    $logsDir = Join-Path $DataDirectory "logs"
    $crashLogs = @(
        "startup-crash.log",
        "broker-crash.log",
        "watchdog-crash.log"
    )

    foreach ($name in $crashLogs) {
        $path = Join-Path $logsDir $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Add-ProbeResult -Name "crash-log:$name" -Ok $true -Detail "absent"
            continue
        }

        $item = Get-Item -LiteralPath $path
        $metadata = @{
            bytes = $item.Length
            lastWriteUtc = $item.LastWriteTimeUtc.ToString("o")
        }
        if ($item.Length -gt 0) {
            $metadata.sha256Prefix = Get-HashPrefix $path
        }

        Add-ProbeResult `
            -Name "crash-log:$name" `
            -Ok ($item.Length -eq 0) `
            -Detail $(if ($item.Length -eq 0) { "empty" } else { "present" }) `
            -Metadata $metadata
    }
}

function Test-HelperAttestation {
    param([string]$DataDirectory)

    $path = Join-Path $DataDirectory "helper-attestations.json"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Add-ProbeResult -Name "helper:attestation" -Ok $false -Detail "missing"
        return
    }

    try {
        $doc = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $helpers = @($doc.helpers)
        $ok =
            [int]$doc.version -eq 1 -and
            -not [string]::IsNullOrWhiteSpace([string]$doc.pipeNonce) -and
            $helpers.Count -gt 0

        Add-ProbeResult `
            -Name "helper:attestation" `
            -Ok $ok `
            -Detail $(if ($ok) { "present" } else { "invalid" }) `
            -Metadata @{ helperCount = $helpers.Count; valuesRedacted = $true }
    } catch {
        Add-ProbeResult -Name "helper:attestation" -Ok $false -Detail "unreadable"
    }
}

function Test-AccessRuleHasRight {
    param(
        [object[]]$Rules,
        [System.Security.AccessControl.FileSystemRights]$Right
    )

    foreach ($rule in @($Rules)) {
        if (($rule.FileSystemRights -band $Right) -eq $Right) {
            return $true
        }
    }
    return $false
}

function Test-AppSettingsAcl {
    param([string]$Directory)

    $configPath = Join-Path $Directory "appsettings.json"
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        Add-ProbeResult -Name "appsettings:acl-localservice-modify" -Ok $false -Detail "config_missing"
        Add-ProbeResult -Name "appsettings:acl-networkservice-readonly" -Ok $false -Detail "config_missing"
        return
    }

    try {
        $acl = Get-Acl -LiteralPath $configPath
        $localRules = @($acl.Access | Where-Object {
            [string]$_.IdentityReference -match "LOCAL SERVICE" -and
            $_.AccessControlType -eq "Allow"
        })
        $networkRules = @($acl.Access | Where-Object {
            [string]$_.IdentityReference -match "NETWORK SERVICE" -and
            $_.AccessControlType -eq "Allow"
        })

        $localCanSeal = Test-AccessRuleHasRight -Rules $localRules -Right ([System.Security.AccessControl.FileSystemRights]::Modify)
        Add-ProbeResult `
            -Name "appsettings:acl-localservice-modify" `
            -Ok $localCanSeal `
            -Detail $(if ($localCanSeal) { "can_dpapi_seal" } else { "missing_modify" }) `
            -Metadata @{ valuesRedacted = $true }

        $networkCanRead = Test-AccessRuleHasRight -Rules $networkRules -Right ([System.Security.AccessControl.FileSystemRights]::Read)
        $networkCanModify = Test-AccessRuleHasRight -Rules $networkRules -Right ([System.Security.AccessControl.FileSystemRights]::Modify)
        $networkCanWrite = Test-AccessRuleHasRight -Rules $networkRules -Right ([System.Security.AccessControl.FileSystemRights]::Write)
        $networkFull = Test-AccessRuleHasRight -Rules $networkRules -Right ([System.Security.AccessControl.FileSystemRights]::FullControl)
        $networkReadOnly = $networkCanRead -and -not $networkCanModify -and -not $networkCanWrite -and -not $networkFull
        Add-ProbeResult `
            -Name "appsettings:acl-networkservice-readonly" `
            -Ok $networkReadOnly `
            -Detail $(if ($networkReadOnly) { "read_only" } else { "too_broad_or_missing" }) `
            -Metadata @{ valuesRedacted = $true }
    } catch {
        Add-ProbeResult -Name "appsettings:acl-localservice-modify" -Ok $false -Detail "acl_unreadable"
        Add-ProbeResult -Name "appsettings:acl-networkservice-readonly" -Ok $false -Detail "acl_unreadable"
    }
}

function Test-HeartbeatReadiness {
    param(
        [string]$Directory,
        [string]$DataDirectory
    )

    $configPath = Join-Path $Directory "appsettings.json"
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        Add-ProbeResult -Name "heartbeat:config" -Ok $false -Detail "config_missing"
        return
    }

    try {
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        $agent = $config.Agent
        $ready =
            $agent -and
            -not [string]::IsNullOrWhiteSpace([string]$agent.AgentId) -and
            -not [string]::IsNullOrWhiteSpace([string]$agent.PharmacyId) -and
            -not [string]::IsNullOrWhiteSpace([string]$agent.ApiKey) -and
            -not [string]::IsNullOrWhiteSpace([string]$agent.CloudUrl)

        Add-ProbeResult `
            -Name "heartbeat:config" `
            -Ok $ready `
            -Detail $(if ($ready) { "ready" } else { "missing_required_fields" }) `
            -Metadata @{ valuesRedacted = $true }
    } catch {
        Add-ProbeResult -Name "heartbeat:config" -Ok $false -Detail "config_unreadable"
    }

    $healthPath = Join-Path $DataDirectory "config-sync-health.json"
    if (Test-Path -LiteralPath $healthPath -PathType Leaf) {
        try {
            $health = Get-Content -LiteralPath $healthPath -Raw | ConvertFrom-Json
            $healthy = [string]$health.status -eq "success" -or [string]$health.status -eq "ok"
            Add-ProbeResult `
                -Name "heartbeat:config-sync-health" `
                -Ok $healthy `
                -Detail ([string]$health.status) `
                -Metadata @{ valuesRedacted = $true }
        } catch {
            Add-ProbeResult -Name "heartbeat:config-sync-health" -Ok $false -Detail "health_unreadable"
        }
    } else {
        Add-ProbeResult -Name "heartbeat:config-sync-health" -Ok $true -Detail "not_yet_written"
    }

    $cloudAuthHealthPath = Join-Path $DataDirectory "cloud-auth-health.json"
    if (Test-Path -LiteralPath $cloudAuthHealthPath -PathType Leaf) {
        try {
            $cloudAuth = Get-Content -LiteralPath $cloudAuthHealthPath -Raw | ConvertFrom-Json
            $status = [string]$cloudAuth.status
            $healthy = $status -eq "success" -or $status -eq "ok" -or $status -eq "recovered"
            Add-ProbeResult `
                -Name "heartbeat:cloud-auth-health" `
                -Ok $healthy `
                -Detail $status `
                -Metadata @{
                    valuesRedacted = $true
                    lastErrorKind = [string]$cloudAuth.lastErrorKind
                    recoveryAttempted = [bool]$cloudAuth.recoveryAttempted
                    recoveryOutcome = [string]$cloudAuth.recoveryOutcome
                    restartRequested = [bool]$cloudAuth.restartRequested
                    consecutiveFailures = [int]$cloudAuth.consecutiveFailures
                }
        } catch {
            Add-ProbeResult -Name "heartbeat:cloud-auth-health" -Ok $false -Detail "health_unreadable"
        }
    } else {
        Add-ProbeResult -Name "heartbeat:cloud-auth-health" -Ok $true -Detail "not_yet_written"
    }
}

function Unprotect-AgentSecret {
    param([AllowNull()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $Value
    }

    if (-not $Value.StartsWith("DPAPI:", [System.StringComparison]::Ordinal)) {
        return $Value
    }

    Add-Type -AssemblyName System.Security
    $bytes = [Convert]::FromBase64String($Value.Substring(6))
    $plain = [System.Security.Cryptography.ProtectedData]::Unprotect(
        $bytes,
        $null,
        [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
    return [System.Text.Encoding]::UTF8.GetString($plain)
}

function Get-AgentHmacSignature {
    param(
        [string]$ApiKey,
        [string]$Timestamp,
        [string]$Body
    )

    $hmac = New-Object System.Security.Cryptography.HMACSHA256
    $hmac.Key = [System.Text.Encoding]::UTF8.GetBytes($ApiKey)
    $signed = $Timestamp + ":" + $Body
    $bytes = $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($signed))
    return ([System.BitConverter]::ToString($bytes) -replace "-", "").ToLowerInvariant()
}

function Get-CloudAuthFailureDetail {
    param([object]$ErrorRecord)

    $status = $null
    $reason = $null

    if ($ErrorRecord.Exception.Response) {
        try { $status = [int]$ErrorRecord.Exception.Response.StatusCode } catch { }

        try {
            $stream = $ErrorRecord.Exception.Response.GetResponseStream()
            if ($stream) {
                $reader = New-Object System.IO.StreamReader($stream)
                $body = $reader.ReadToEnd()
                if (-not [string]::IsNullOrWhiteSpace($body)) {
                    $parsed = $body | ConvertFrom-Json
                    if ($parsed.error) {
                        $reason = ([string]$parsed.error) -replace '[^A-Za-z0-9_.-]', '_'
                        if ($reason.Length -gt 80) {
                            $reason = $reason.Substring(0, 80)
                        }
                    }
                }
            }
        } catch {
            $reason = $null
        }
    }

    if ($status -and $reason) {
        return "http_$status`_$reason"
    }
    if ($status) {
        return "http_$status"
    }
    return "request_failed"
}

function Test-AgentCloudAuth {
    param([string]$Directory)

    $configPath = Join-Path $Directory "appsettings.json"
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        Add-ProbeResult -Name "cloud-auth:agent-config" -Ok $false -Detail "config_missing"
        return
    }

    try {
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        $agent = $config.Agent
        $cloudUrl = ([string]$agent.CloudUrl).TrimEnd([char[]]"/")
        $apiKey = Unprotect-AgentSecret -Value ([string]$agent.ApiKey)

        if ([string]::IsNullOrWhiteSpace($cloudUrl) -or [string]::IsNullOrWhiteSpace($apiKey)) {
            Add-ProbeResult -Name "cloud-auth:agent-config" -Ok $false -Detail "missing_required_fields" -Metadata @{ valuesRedacted = $true }
            return
        }

        if (-not $cloudUrl.StartsWith("https://", [System.StringComparison]::OrdinalIgnoreCase)) {
            Add-ProbeResult -Name "cloud-auth:agent-config" -Ok $false -Detail "cloud_url_not_https" -Metadata @{ valuesRedacted = $true }
            return
        }

        $timestamp = [DateTimeOffset]::UtcNow.ToString("o")
        $headers = @{
            "x-agent-api-key" = $apiKey
            "x-agent-timestamp" = $timestamp
            "x-agent-signature" = Get-AgentHmacSignature -ApiKey $apiKey -Timestamp $timestamp -Body ""
        }

        $response = Invoke-RestMethod `
            -Method GET `
            -Uri "$cloudUrl/api/agent/config" `
            -Headers $headers `
            -TimeoutSec 15

        $ok = $response -and [bool]$response.success
        Add-ProbeResult `
            -Name "cloud-auth:agent-config" `
            -Ok $ok `
            -Detail $(if ($ok) { "accepted" } else { "unexpected_response" }) `
            -Metadata @{ valuesRedacted = $true }
    } catch {
        $detail = Get-CloudAuthFailureDetail -ErrorRecord $_
        Add-ProbeResult `
            -Name "cloud-auth:agent-config" `
            -Ok $false `
            -Detail $detail `
            -Metadata @{ valuesRedacted = $true }
    } finally {
        $apiKey = $null
    }
}

function Resolve-ReleaseDirectory {
    if ($ReleaseZip) {
        if (-not (Test-Path -LiteralPath $ReleaseZip -PathType Leaf)) {
            throw "ReleaseZip not found: $ReleaseZip"
        }
        $expanded = Join-Path ([System.IO.Path]::GetTempPath()) ("suavoagent-release-smoke-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $expanded -Force | Out-Null
        Expand-Archive -LiteralPath $ReleaseZip -DestinationPath $expanded -Force
        return $expanded
    }

    return $ReleaseDir
}

try {
    if ($Mode -eq "ReleaseArtifact") {
        $dir = Resolve-ReleaseDirectory
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
            throw "ReleaseDir not found: $dir"
        }

        Test-ReleaseBinaries -Directory $dir
        if (-not $BootstrapPath) {
            $BootstrapPath = Join-Path (Get-Location) "bootstrap.ps1"
        }
        Test-BootstrapRepairPath -Path $BootstrapPath
    } else {
        Test-ReleaseBinaries -Directory $InstallDir
        Test-InstalledServices
        Test-CrashLogMarkers -DataDirectory $ProgramDataDir
        Test-HelperAttestation -DataDirectory $ProgramDataDir
        if (-not $BootstrapPath) {
            $BootstrapPath = Join-Path $ProgramDataDir "bootstrap.ps1"
        }
        Test-BootstrapRepairPath -Path $BootstrapPath
        Test-AppSettingsAcl -Directory $InstallDir
        Test-HeartbeatReadiness -Directory $InstallDir -DataDirectory $ProgramDataDir
        Test-AgentCloudAuth -Directory $InstallDir
    }

    $failed = @($results | Where-Object { -not $_.ok })
    if ($Json) {
        [pscustomobject]@{
            ok = ($failed.Count -eq 0)
            mode = $Mode
            checkedAt = (Get-Date).ToUniversalTime().ToString("o")
            results = $results
        } | ConvertTo-Json -Depth 6
    } else {
        foreach ($result in $results) {
            $mark = if ($result.ok) { "OK" } else { "FAIL" }
            Write-Host ("{0} {1} - {2}" -f $mark, $result.name, $result.detail)
        }
    }

    if ($failed.Count -gt 0) {
        exit 1
    }
} catch {
    if ($Json) {
        [pscustomobject]@{
            ok = $false
            mode = $Mode
            checkedAt = (Get-Date).ToUniversalTime().ToString("o")
            error = $_.Exception.Message
        } | ConvertTo-Json -Depth 4
    } else {
        Write-Host ("FAIL smoke-probe - {0}" -f $_.Exception.Message)
    }
    exit 1
}
