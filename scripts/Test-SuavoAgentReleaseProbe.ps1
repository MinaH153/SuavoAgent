#Requires -Version 5.1
<#
.SYNOPSIS
    No-PHI SuavoAgent release and installed-machine smoke probe.
.DESCRIPTION
    ReleaseArtifact mode validates a staged release directory or zip before a
    GitHub release is published. Installed mode validates the local Windows
    install: services, binaries, native maintenance host, and heartbeat readiness
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
    [string]$MaintenancePath,
    [string]$AllowedSignerSha256,
    [switch]$RequireAuthenticodeSignature,
    [switch]$Json
)

$ErrorActionPreference = "Stop"
$expectedPublisher = "MKM TECHNOLOGIES LLC"
$allowedSignerDigests = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
if (-not [string]::IsNullOrWhiteSpace($AllowedSignerSha256)) {
    foreach ($candidate in $AllowedSignerSha256.Split(
        [char[]]@(',', ';'),
        [System.StringSplitOptions]::None)) {
        $normalized = $candidate.Trim().ToUpperInvariant()
        if ($normalized -notmatch '^[A-F0-9]{64}$' -or -not $allowedSignerDigests.Add($normalized)) {
            throw "AllowedSignerSha256 must contain unique SHA-256 certificate digests."
        }
    }
}
if ($RequireAuthenticodeSignature -and $allowedSignerDigests.Count -eq 0) {
    throw "Release signature verification requires an explicit signer SHA-256 allowlist."
}

$runtimeBinaries = @(
    "SuavoAgent.Core.exe",
    "SuavoAgent.Broker.exe",
    "SuavoAgent.Helper.exe",
    "SuavoAgent.Watchdog.exe"
)
$releaseBinaries = @($runtimeBinaries) + @("SuavoSetup.exe")
$installedBinaries = @($runtimeBinaries) + @("SuavoAgent.Maintenance.exe")

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

function Get-CertificateSha256 {
    param([System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($Certificate.RawData))).Replace('-', '')
    } finally {
        $sha.Dispose()
    }
}

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path -LiteralPath $kits -PathType Container)) { return $null }
    return Get-ChildItem -LiteralPath $kits -Directory |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
}

function Test-Rfc3161Timestamp {
    param([string]$Path, [object]$Signature)
    if (-not $Signature.TimeStamperCertificate) { return $false }
    $timestampEku = $false
    foreach ($extension in $Signature.TimeStamperCertificate.Extensions) {
        if ($extension -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            foreach ($oid in $extension.EnhancedKeyUsages) {
                if ($oid.Value -eq '1.3.6.1.5.5.7.3.8') { $timestampEku = $true }
            }
        }
    }
    if (-not $timestampEku) { return $false }
    $signtool = Find-SignTool
    if (-not $signtool) { return $false }
    $output = & $signtool verify /pa /all /tw $Path 2>&1
    return ($LASTEXITCODE -eq 0)
}

function Test-ExactSignature {
    param([string]$Path, [object]$Signature)
    if ($Signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        -not $Signature.SignerCertificate) { return $false }
    $publisher = $Signature.SignerCertificate.GetNameInfo(
        [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName,
        $false)
    $digest = Get-CertificateSha256 $Signature.SignerCertificate
    return $publisher -ceq $expectedPublisher -and
        $allowedSignerDigests.Contains($digest) -and
        (Test-Rfc3161Timestamp $Path $Signature)
}

function Test-Binaries {
    param(
        [string]$Directory,
        [string[]]$Names,
        [bool]$RequireValidSignature
    )

    foreach ($binary in $Names) {
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

        $signature = Get-AuthenticodeSignature -LiteralPath $path
        $signatureMetadata = @{
            status = $signature.Status.ToString()
            signerSha256 = if ($signature.SignerCertificate) { Get-CertificateSha256 $signature.SignerCertificate } else { $null }
            signerSubject = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { $null }
            rfc3161Timestamp = if ($signature.SignerCertificate) { Test-Rfc3161Timestamp $path $signature } else { $false }
        }
        $publisher = if ($signature.SignerCertificate) {
            $signature.SignerCertificate.GetNameInfo(
                [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName,
                $false)
        } else { $null }
        $signatureValid = Test-ExactSignature $path $signature
        Add-ProbeResult `
            -Name "signature:$binary" `
            -Ok ((-not $RequireValidSignature) -or $signatureValid) `
            -Detail $(if ($signatureValid) { "Valid:$publisher" } else { "$($signature.Status):publisher_mismatch" }) `
            -Metadata ($signatureMetadata + @{ publisher = $publisher; expectedPublisher = $expectedPublisher })

        if ($RequireValidSignature -and -not $signatureValid) {
            continue
        }
    }
}

function Test-NativeMaintenanceHost {
    param(
        [string]$Path,
        [string]$ExpectedFileName,
        [bool]$RequireValidSignature
    )

    if (-not $Path) {
        Add-ProbeResult -Name "maintenance:host" -Ok $false -Detail "path_missing"
        return
    }

    if ([System.IO.Path]::GetFileName($Path) -ne $ExpectedFileName) {
        Add-ProbeResult -Name "maintenance:host" -Ok $false -Detail "unexpected_filename"
        return
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-ProbeResult -Name "maintenance:host" -Ok $false -Detail "file_missing"
        return
    }

    $item = Get-Item -LiteralPath $Path
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $publisher = if ($signature.SignerCertificate) {
        $signature.SignerCertificate.GetNameInfo(
            [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName,
            $false)
    } else { $null }
    $signatureValid = Test-ExactSignature $Path $signature
    Add-ProbeResult `
        -Name "maintenance:host" `
        -Ok (($item.Length -gt 0) -and ((-not $RequireValidSignature) -or $signatureValid)) `
        -Detail $signature.Status.ToString() `
        -Metadata @{
            bytes = $item.Length
            sha256Prefix = Get-HashPrefix $Path
            signerSha256 = if ($signature.SignerCertificate) { Get-CertificateSha256 $signature.SignerCertificate } else { $null }
            signerSubject = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { $null }
            publisher = $publisher
            expectedPublisher = $expectedPublisher
            rfc3161Timestamp = if ($signature.SignerCertificate) { Test-Rfc3161Timestamp $Path $signature } else { $false }
        }
}

function Test-InstalledCohortIntegrity {
    param(
        [string]$Directory,
        [string]$DataDirectory
    )

    $statePath = Join-Path $Directory "install-state.json"
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
        Add-ProbeResult -Name "install-state" -Ok $false -Detail "missing"
    } else {
        try {
            $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
            $stateCohort = @($state.installedCohort | ForEach-Object { [string]$_ })
            $cohortMatches = $stateCohort.Count -eq $installedBinaries.Count
            if ($cohortMatches) {
                for ($i = 0; $i -lt $installedBinaries.Count; $i++) {
                    if ($stateCohort[$i] -ne $installedBinaries[$i]) {
                        $cohortMatches = $false
                        break
                    }
                }
            }

            $stateValid =
                [int]$state.schemaVersion -eq 1 -and
                [string]$state.installerKind -eq "native-maintenance-bridge" -and
                [string]$state.maintenanceExecutable -eq "SuavoAgent.Maintenance.exe" -and
                $cohortMatches
            Add-ProbeResult `
                -Name "install-state" `
                -Ok $stateValid `
                -Detail $(if ($stateValid) { "valid" } else { "invalid" }) `
                -Metadata @{ cohortCount = $stateCohort.Count; valuesRedacted = $true }
        } catch {
            Add-ProbeResult -Name "install-state" -Ok $false -Detail "unreadable"
        }
    }

    $manifestPath = Join-Path $DataDirectory "binaries.manifest"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        Add-ProbeResult -Name "binaries-manifest:cohort" -Ok $false -Detail "missing"
        foreach ($binary in $installedBinaries) {
            Add-ProbeResult -Name "manifest-hash:$binary" -Ok $false -Detail "manifest_missing"
        }
        return
    }

    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $properties = @($manifest.PSObject.Properties)
        $unexpected = @($properties | Where-Object { $installedBinaries -notcontains $_.Name })
        $missing = @($installedBinaries | Where-Object { $properties.Name -notcontains $_ })
        $shapeValid =
            $properties.Count -eq $installedBinaries.Count -and
            $unexpected.Count -eq 0 -and
            $missing.Count -eq 0
        Add-ProbeResult `
            -Name "binaries-manifest:cohort" `
            -Ok $shapeValid `
            -Detail $(if ($shapeValid) { "five_entries" } else { "invalid_entries" }) `
            -Metadata @{ entryCount = $properties.Count; valuesRedacted = $true }

        foreach ($binary in $installedBinaries) {
            $binaryPath = Join-Path $Directory $binary
            $property = $manifest.PSObject.Properties[$binary]
            if (-not (Test-Path -LiteralPath $binaryPath -PathType Leaf)) {
                Add-ProbeResult -Name "manifest-hash:$binary" -Ok $false -Detail "binary_missing"
                continue
            }
            if (-not $property) {
                Add-ProbeResult -Name "manifest-hash:$binary" -Ok $false -Detail "entry_missing"
                continue
            }

            $expectedHash = [string]$property.Value
            $expectedValid = $expectedHash -match '^[A-Fa-f0-9]{64}$'
            $actualHash = (Get-FileHash -LiteralPath $binaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
            $hashMatches = $expectedValid -and $actualHash -eq $expectedHash.ToLowerInvariant()
            Add-ProbeResult `
                -Name "manifest-hash:$binary" `
                -Ok $hashMatches `
                -Detail $(if ($hashMatches) { "match" } else { "mismatch" }) `
                -Metadata @{ sha256Prefix = $actualHash.Substring(0, 16); valuesRedacted = $true }
        }
    } catch {
        Add-ProbeResult -Name "binaries-manifest:cohort" -Ok $false -Detail "unreadable"
        foreach ($binary in $installedBinaries) {
            Add-ProbeResult -Name "manifest-hash:$binary" -Ok $false -Detail "manifest_unreadable"
        }
    }
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

function Test-ServiceRunning {
    param([string]$Name)

    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    return ($svc -and $svc.Status -eq "Running")
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

    $configSyncHealthPath = Join-Path $DataDirectory "config-sync-health.json"
    if (Test-Path -LiteralPath $configSyncHealthPath -PathType Leaf) {
        try {
            $health = Get-Content -LiteralPath $configSyncHealthPath -Raw | ConvertFrom-Json
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
        Add-ProbeResult `
            -Name "heartbeat:config-sync-health" `
            -Ok $false `
            -Detail $(if (Test-ServiceRunning -Name "SuavoAgent.Core") { "missing_after_service_running" } else { "missing_after_service_not_running" }) `
            -Metadata @{ serviceName = "SuavoAgent.Core"; valuesRedacted = $true }
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
        Add-ProbeResult `
            -Name "heartbeat:cloud-auth-health" `
            -Ok $true `
            -Detail "no_recovery_evidence" `
            -Metadata @{ valuesRedacted = $true }
    }

    $watchdogHealthPath = Join-Path $DataDirectory "watchdog-health.json"
    if (Test-Path -LiteralPath $watchdogHealthPath -PathType Leaf) {
        try {
            $watchdog = Get-Content -LiteralPath $watchdogHealthPath -Raw | ConvertFrom-Json
            $services = @($watchdog.services)
            $healthy =
                [bool]$watchdog.present -and
                -not [string]::IsNullOrWhiteSpace([string]$watchdog.timestamp) -and
                $services.Count -gt 0

            Add-ProbeResult `
                -Name "heartbeat:watchdog-health" `
                -Ok $healthy `
                -Detail $(if ($healthy) { "present" } else { "invalid" }) `
                -Metadata @{ serviceCount = $services.Count; valuesRedacted = $true }
        } catch {
            Add-ProbeResult -Name "heartbeat:watchdog-health" -Ok $false -Detail "health_unreadable"
        }
    } else {
        Add-ProbeResult `
            -Name "heartbeat:watchdog-health" `
            -Ok $false `
            -Detail $(if (Test-ServiceRunning -Name "SuavoAgent.Watchdog") { "missing_after_service_running" } else { "missing_after_service_not_running" }) `
            -Metadata @{ serviceName = "SuavoAgent.Watchdog"; valuesRedacted = $true }
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

$legalProbeSupport = Join-Path $PSScriptRoot 'Test-SuavoAgentReleaseProbe.Legal.ps1'
if (-not (Test-Path -LiteralPath $legalProbeSupport -PathType Leaf)) {
    throw "Release legal probe support is missing: $legalProbeSupport"
}
. $legalProbeSupport
if (-not (Get-Command Test-ReleaseLegalBundle -CommandType Function -ErrorAction SilentlyContinue)) {
    throw "Release legal probe support did not define Test-ReleaseLegalBundle."
}

try {
    if ($Mode -eq "ReleaseArtifact") {
        $dir = Resolve-ReleaseDirectory
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
            throw "ReleaseDir not found: $dir"
        }

        Test-Binaries `
            -Directory $dir `
            -Names $releaseBinaries `
            -RequireValidSignature ([bool]$RequireAuthenticodeSignature)
        if (-not $MaintenancePath) {
            $MaintenancePath = Join-Path $dir "SuavoSetup.exe"
        }
        Test-NativeMaintenanceHost `
            -Path $MaintenancePath `
            -ExpectedFileName "SuavoSetup.exe" `
            -RequireValidSignature ([bool]$RequireAuthenticodeSignature)
        Test-ReleaseLegalBundle -Directory $dir
    } else {
        # Installed evidence is always fail-closed: all five installed executables
        # must have valid Authenticode, regardless of the optional release-artifact switch.
        Test-Binaries `
            -Directory $InstallDir `
            -Names $installedBinaries `
            -RequireValidSignature $true
        Test-InstalledCohortIntegrity -Directory $InstallDir -DataDirectory $ProgramDataDir
        Test-InstalledServices
        Test-CrashLogMarkers -DataDirectory $ProgramDataDir
        Test-HelperAttestation -DataDirectory $ProgramDataDir
        if (-not $MaintenancePath) {
            $MaintenancePath = Join-Path $InstallDir "SuavoAgent.Maintenance.exe"
        }
        Test-NativeMaintenanceHost `
            -Path $MaintenancePath `
            -ExpectedFileName "SuavoAgent.Maintenance.exe" `
            -RequireValidSignature $true
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
