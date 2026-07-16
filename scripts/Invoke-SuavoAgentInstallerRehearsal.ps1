#Requires -Version 7.2
<#
.SYNOPSIS
    Runs the PHI-negative install, repair, and uninstall gate for one signed installer.
.DESCRIPTION
    This is an engineering gate for a disposable Windows 11 x64 machine. It
    refuses a pre-existing SuavoAgent service cohort, verifies the exact signed
    installer inputs, performs a fresh install, proves the installed five-file
    cohort and service configuration, deletes Helper and the Maintenance host
    to exercise current-MSI BinaryRef repair, proves repair did not mint a new
    Release 1 install marker, and then uninstalls.

    The JSON evidence deliberately excludes machine name, user name, paths,
    configuration values, logs, and exception text. Verbose Windows Installer
    logs remain separate evidence files in EvidenceDirectory.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Msi', 'Bundle')]
    [string]$InstallerKind,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$InstallerPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$MsiPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$ExpectedReleaseTag,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Fa-f0-9]{64}(,[A-Fa-f0-9]{64}){0,15}$')]
    [string]$AllowedSignerSha256,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$EvidenceDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$serviceNames = @(
    'SuavoAgent.Core',
    'SuavoAgent.Broker',
    'SuavoAgent.Watchdog'
)
$installedNames = @(
    'SuavoAgent.Core.exe',
    'SuavoAgent.Broker.exe',
    'SuavoAgent.Helper.exe',
    'SuavoAgent.Watchdog.exe',
    'SuavoAgent.Maintenance.exe'
)
$installDirectory = Join-Path $env:ProgramFiles 'Suavo\Agent'
$dataDirectory = Join-Path $env:ProgramData 'SuavoAgent'
$proofDirectory = Join-Path $env:ProgramData 'SuavoAgent-InstallerProof'
$markerPath = Join-Path $proofDirectory 'release1-msi-install-commit.json'
$markerRollbackJournalPath = Join-Path $proofDirectory '.msi-release1-marker.rollback.json'
$serviceRollbackJournalPath = Join-Path $installDirectory '.msi-service-hardening.rollback.json'
$transactionActivePath = Join-Path $installDirectory '.msi-installer-transaction.active.json'
$legacyProductRegistryPaths = @(
    'HKLM:\SOFTWARE\SuavoAgent',
    'HKLM:\SOFTWARE\MKM Technologies LLC\SuavoAgent',
    'HKLM:\SOFTWARE\Classes\suavoagent',
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SuavoAgent',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\SuavoAgent',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SuavoAgent'
)
$uninstallRegistryRoots = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
)
$legacyInstallDirectories = @(
    (Join-Path ([IO.Path]::GetPathRoot($env:SystemRoot)) 'SuavoAgent'),
    (Join-Path $env:ProgramFiles 'SuavoAgent')
)
$retentionSentinelPath = Join-Path $dataDirectory 'installer-rehearsal-retention.sentinel'
$phaseResults = [System.Collections.Generic.List[string]]::new()
$installedHashes = [ordered]@{}
$artifactSha256 = $null
$msiSha256 = $null
$markerSha256 = $null
$markerTransactionId = $null
$failureCode = $null
$scriptStartedAtUtc = [DateTimeOffset]::UtcNow
$startedAtUtc = $scriptStartedAtUtc.ToString('o')
$markerFreshnessDeadlineUtc = $scriptStartedAtUtc.AddMinutes(30)
$preInstallMarkerSha256 = $null
$preInstallMarkerTransactionId = $null
$retentionSentinelSha256 = $null

function Assert-Condition {
    param([bool]$Condition, [string]$Code)
    if (-not $Condition) { throw [InvalidOperationException]::new($Code) }
}

function Get-RegularFile {
    param([string]$Path, [string]$Code)
    $item = Get-Item -LiteralPath $Path -Force
    Assert-Condition (-not $item.PSIsContainer) "$Code`:not_file"
    Assert-Condition (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "$Code`:reparse_point"
    Assert-Condition ($item.Length -gt 0) "$Code`:empty"
    return $item
}

function Get-Sha256 {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Invoke-CheckedProcess {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [int[]]$AllowedExitCodes,
        [string]$Code
    )
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -Wait -PassThru
    Assert-Condition ($process.ExitCode -in $AllowedExitCodes) "$Code`:exit_$($process.ExitCode)"
}

function Wait-ForServiceState {
    param([string]$Name, [string]$State)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($service -and [string]$service.Status -eq $State) { return $service }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw [InvalidOperationException]::new("service_state_invalid:$Name`:$State")
}

function Test-ExactLegacyBrokerPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or
        $Path.Length -gt 2048 -or
        $Path.IndexOfAny([char[]]"`r`n") -ge 0) { return $false }
    $normalized = $Path.Trim().Trim('"').Replace('/', '\')
    return $normalized -match '^[A-Za-z]:\\Users\\[^\\]+\\suavo-publish\\Broker\\SuavoAgent\.Broker\.exe$'
}

function Test-TrustedLegacyCommandHost {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    $normalized = $Path.Trim().Trim('"').Replace('/', '\')
    $windows = $env:SystemRoot.Trim().Trim('"').Replace('/', '\').TrimEnd('\')
    return $normalized -ieq (Join-Path $windows 'System32\cmd.exe') -or
        $normalized -ieq (Join-Path $windows 'Sysnative\cmd.exe')
}

function Get-LegacyShortcutCandidates {
    $roots = @(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonDesktopDirectory)
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    return @($roots | ForEach-Object {
        Join-Path $_ 'Suavo.lnk'
        Join-Path $_ 'Suavo\Suavo.lnk'
    } | Sort-Object -Unique)
}

function Test-ExactLegacyShortcut {
    param([string]$Path)
    $shell = $null
    try {
        Get-RegularFile $Path 'legacy_shortcut' | Out-Null
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($Path)
        $target = [string]$shortcut.TargetPath
        $arguments = [string]$shortcut.Arguments
        if (Test-ExactLegacyBrokerPath $target) { return $true }
        if (-not (Test-TrustedLegacyCommandHost $target)) { return $false }
        return $arguments -match '(?i)"?[A-Za-z]:\\Users\\[^"\\\r\n]+\\suavo-publish\\Broker\\SuavoAgent\.Broker\.exe"?'
    } catch {
        throw [InvalidOperationException]::new('legacy_shortcut_probe_failed')
    } finally {
        if ($null -ne $shell) {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
        }
    }
}

function Assert-NoLegacyInteractiveState {
    $processes = @()
    try {
        $processes = @(Get-CimInstance Win32_Process `
            -Filter "Name='SuavoAgent.Broker.exe'" -ErrorAction Stop)
    } catch {
        throw [InvalidOperationException]::new('legacy_process_probe_failed')
    }
    foreach ($process in $processes) {
        $path = [string]$process.ExecutablePath
        Assert-Condition (-not [string]::IsNullOrWhiteSpace($path)) `
            'legacy_process_path_unavailable'
        Assert-Condition (-not (Test-ExactLegacyBrokerPath $path)) `
            'legacy_process_present'
    }
    foreach ($shortcut in (Get-LegacyShortcutCandidates)) {
        if (Test-Path -LiteralPath $shortcut) {
            Assert-Condition (-not (Test-ExactLegacyShortcut $shortcut)) `
                'legacy_shortcut_present'
        }
    }
}

function Assert-NoKnownLegacyProductState {
    foreach ($path in $legacyProductRegistryPaths) {
        Assert-Condition (-not (Test-Path -LiteralPath $path)) `
            'legacy_product_registry_present'
    }
    foreach ($root in $uninstallRegistryRoots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($entry in @(Get-ChildItem -LiteralPath $root -ErrorAction Stop)) {
            $product = Get-ItemProperty -LiteralPath $entry.PSPath -ErrorAction Stop
            Assert-Condition ([string]$product.DisplayName -cne 'SuavoAgent') `
                'legacy_installed_product_present'
        }
    }
    foreach ($path in $legacyInstallDirectories) {
        Assert-Condition (-not (Test-Path -LiteralPath $path)) `
            'legacy_product_directory_present'
    }
    $legacyTask = Get-ScheduledTask -TaskPath '\' -TaskName 'SuavoSelfUninstall' `
        -ErrorAction SilentlyContinue
    Assert-Condition ($null -eq $legacyTask) 'legacy_product_scheduled_task_present'
}

function Assert-InstalledState {
    param([string]$ExpectedMsiHash)

    $manifestPath = Join-Path $dataDirectory 'binaries.manifest'
    Get-RegularFile $manifestPath 'manifest' | Out-Null
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $properties = @($manifest.PSObject.Properties)
    Assert-Condition ($properties.Count -eq $installedNames.Count) 'manifest:entry_count'

    foreach ($name in $installedNames) {
        $path = Join-Path $installDirectory $name
        Get-RegularFile $path "installed:$name" | Out-Null
        & (Join-Path $PSScriptRoot 'Test-InstallerAuthenticode.ps1') `
            -Path $path -AllowedSignerSha256 $AllowedSignerSha256 | Out-Null
        $actual = Get-Sha256 $path
        $entry = $manifest.PSObject.Properties[$name]
        Assert-Condition ($null -ne $entry) "manifest:missing:$name"
        Assert-Condition ([string]$entry.Value -ceq $actual) "manifest:mismatch:$name"
        $installedHashes[$name] = $actual
    }

    $unexpectedExecutables = @(Get-ChildItem -LiteralPath $installDirectory -Filter '*.exe' -File |
        Where-Object { $_.Name -notin $installedNames })
    Assert-Condition ($unexpectedExecutables.Count -eq 0) 'installed:unexpected_executable'

    $statePath = Join-Path $installDirectory 'install-state.json'
    Get-RegularFile $statePath 'install_state' | Out-Null
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    Assert-Condition ([int]$state.schemaVersion -eq 1) 'install_state:schema'
    Assert-Condition ([string]$state.version -ceq $ExpectedReleaseTag.Substring(1)) 'install_state:version'
    Assert-Condition ([string]$state.maintenanceExecutable -ceq 'SuavoAgent.Maintenance.exe') 'install_state:maintenance'

    $accounts = @{
        'SuavoAgent.Core' = 'NT AUTHORITY\LocalService'
        'SuavoAgent.Broker' = 'LocalSystem'
        'SuavoAgent.Watchdog' = 'LocalSystem'
    }
    foreach ($name in $serviceNames) {
        Wait-ForServiceState $name 'Running' | Out-Null
        $service = Get-CimInstance Win32_Service -Filter "Name='$name'"
        Assert-Condition ($null -ne $service) "service:missing:$name"
        Assert-Condition ([string]$service.StartMode -ceq 'Auto') "service:start_mode:$name"
        Assert-Condition ([string]$service.StartName -ceq $accounts[$name]) "service:account:$name"
        $registry = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$name"
        Assert-Condition ([int]$registry.DelayedAutostart -eq 1) "service:delayed_auto:$name"
        Assert-Condition ([int]$registry.ServiceSidType -eq 1) "service:sid_type:$name"
        Assert-Condition ($null -ne $registry.FailureActions) "service:failure_actions:$name"
    }
    $brokerRegistry = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\SuavoAgent.Broker'
    Assert-Condition (@($brokerRegistry.DependOnService) -contains 'SuavoAgent.Core') 'service:broker_dependency'
    Assert-Condition (-not (Test-Path -LiteralPath $serviceRollbackJournalPath)) 'transaction:service_journal_remained'
    Assert-Condition (-not (Test-Path -LiteralPath $markerRollbackJournalPath)) 'transaction:marker_journal_remained'
    Assert-Condition (-not (Test-Path -LiteralPath $transactionActivePath)) 'transaction:active_token_remained'

    Get-RegularFile $markerPath 'release1_marker' | Out-Null
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    Assert-Condition ([int]$marker.schemaVersion -eq 2) 'release1_marker:schema'
    Assert-Condition ([string]$marker.purpose -ceq 'suavoagent-msi-install-commit-marker') 'release1_marker:purpose'
    Assert-Condition ([string]$marker.installedReleaseTag -ceq $ExpectedReleaseTag) 'release1_marker:release'
    Assert-Condition ([string]$marker.installerArtifactSha256 -ceq $ExpectedMsiHash) 'release1_marker:msi_hash'
    Assert-Condition ([string]$marker.maintenanceHostSha256 -ceq $installedHashes['SuavoAgent.Maintenance.exe']) 'release1_marker:maintenance_hash'
    Assert-Condition ([string]$marker.installTransactionId -match '^[a-f0-9]{64}$') 'release1_marker:transaction'
    Assert-Condition ([string]$marker.productCode -match '^\{[A-F0-9-]{36}\}$') 'release1_marker:product_code'
    return $marker
}

function Invoke-InstallOrRepair {
    param([bool]$Repair, [string]$LogPath)
    if ($InstallerKind -eq 'Msi') {
        $operation = if ($Repair) { '/fa' } else { '/i' }
        Invoke-CheckedProcess 'msiexec.exe' @(
            $operation, "`"$($script:msiItem.FullName)`"", '/qn', '/norestart', '/L*v', "`"$LogPath`""
        ) @(0, 3010) $(if ($Repair) { 'repair' } else { 'install' })
        return
    }
    $operation = if ($Repair) { '/repair' } else { '/install' }
    Invoke-CheckedProcess $script:installerItem.FullName @(
        $operation, '/quiet', '/norestart', "/log", "`"$LogPath`""
    ) @(0, 1641, 3010) $(if ($Repair) { 'repair' } else { 'install' })
}

Assert-Condition ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [Runtime.InteropServices.OSPlatform]::Windows)) 'host:not_windows'
$principal = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent())
Assert-Condition ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) 'host:not_elevated'

$installerItem = Get-RegularFile $InstallerPath 'installer'
$msiItem = Get-RegularFile $MsiPath 'msi'
Assert-Condition ($msiItem.Extension -ieq '.msi') 'msi:extension'
if ($InstallerKind -eq 'Msi') {
    Assert-Condition ($installerItem.FullName -ceq $msiItem.FullName) 'msi:path_mismatch'
} else {
    Assert-Condition ($installerItem.Name -ceq 'SuavoAgent-Setup.exe') 'bundle:name'
}

New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$evidenceItem = Get-Item -LiteralPath $EvidenceDirectory -Force
Assert-Condition (($evidenceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'evidence:reparse_point'
$installLog = Join-Path $evidenceItem.FullName 'install.log'
$repairLog = Join-Path $evidenceItem.FullName 'repair.log'
$uninstallLog = Join-Path $evidenceItem.FullName 'uninstall.log'
$evidencePath = Join-Path $evidenceItem.FullName 'installer-rehearsal-evidence.json'

try {
    foreach ($name in $serviceNames) {
        Assert-Condition ($null -eq (Get-Service -Name $name -ErrorAction SilentlyContinue)) "preexisting_service:$name"
    }
    Assert-NoLegacyInteractiveState
    Assert-NoKnownLegacyProductState
    Assert-Condition (-not (Test-Path -LiteralPath $installDirectory)) 'preexisting_install_directory'
    Assert-Condition (-not (Test-Path -LiteralPath $markerRollbackJournalPath)) `
        'preexisting_marker_transaction_journal'
    Assert-Condition (-not (Test-Path -LiteralPath $serviceRollbackJournalPath)) `
        'preexisting_service_transaction_journal'
    Assert-Condition (-not (Test-Path -LiteralPath $transactionActivePath)) `
        'preexisting_transaction_active_token'

    if (Test-Path -LiteralPath $markerPath) {
        Get-RegularFile $markerPath 'preexisting_release1_marker' | Out-Null
        $preInstallMarkerSha256 = Get-Sha256 $markerPath
        $preInstallMarker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
        $preInstallMarkerTransactionId = [string]$preInstallMarker.installTransactionId
        Assert-Condition `
            ($preInstallMarkerTransactionId -match '^[a-f0-9]{64}$') `
            'preexisting_release1_marker:transaction'
    }

    & (Join-Path $PSScriptRoot 'Test-InstallerAuthenticode.ps1') `
        -Path $installerItem.FullName -AllowedSignerSha256 $AllowedSignerSha256 | Out-Null
    if ($installerItem.FullName -cne $msiItem.FullName) {
        & (Join-Path $PSScriptRoot 'Test-InstallerAuthenticode.ps1') `
            -Path $msiItem.FullName -AllowedSignerSha256 $AllowedSignerSha256 | Out-Null
    }
    $artifactSha256 = Get-Sha256 $installerItem.FullName
    $msiSha256 = Get-Sha256 $msiItem.FullName
    $phaseResults.Add('signed-inputs-verified')

    Invoke-InstallOrRepair $false $installLog
    $marker = Assert-InstalledState $msiSha256
    $markerSha256 = Get-Sha256 $markerPath
    $markerTransactionId = [string]$marker.installTransactionId
    $markerCompletedAtUtc = [DateTimeOffset]::MinValue
    $markerTimeParsed = [DateTimeOffset]::TryParseExact(
        [string]$marker.installCompletedAtUtc,
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal -bor
            [Globalization.DateTimeStyles]::AdjustToUniversal,
        [ref]$markerCompletedAtUtc)
    Assert-Condition $markerTimeParsed 'fresh_install:completion_invalid'
    Assert-Condition `
        ($markerCompletedAtUtc -ge $scriptStartedAtUtc.AddSeconds(-2)) `
        'fresh_install:completion_before_rehearsal'
    Assert-Condition `
        ($markerCompletedAtUtc -le $markerFreshnessDeadlineUtc) `
        'fresh_install:completion_after_deadline'
    Assert-Condition `
        ($markerCompletedAtUtc -le [DateTimeOffset]::UtcNow.AddSeconds(2)) `
        'fresh_install:completion_in_future'
    if ($null -ne $preInstallMarkerSha256) {
        Assert-Condition `
            ($markerSha256 -cne $preInstallMarkerSha256) `
            'fresh_install:marker_hash_reused'
        Assert-Condition `
            ($markerTransactionId -cne $preInstallMarkerTransactionId) `
            'fresh_install:transaction_reused'
    }
    $dataDirectoryItem = Get-Item -LiteralPath $dataDirectory -Force
    Assert-Condition $dataDirectoryItem.PSIsContainer 'retention_sentinel:data_not_directory'
    Assert-Condition `
        (($dataDirectoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
        'retention_sentinel:data_reparse_point'
    if (Test-Path -LiteralPath $retentionSentinelPath) {
        Get-RegularFile $retentionSentinelPath 'retention_sentinel:preexisting' | Out-Null
    }
    [IO.File]::WriteAllBytes(
        $retentionSentinelPath,
        [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    Get-RegularFile $retentionSentinelPath 'retention_sentinel:seeded' | Out-Null
    $retentionSentinelSha256 = Get-Sha256 $retentionSentinelPath
    $phaseResults.Add('fresh-install-verified')

    Stop-Service -Name 'SuavoAgent.Broker' -Force
    Get-Process -Name 'SuavoAgent.Helper' -ErrorAction SilentlyContinue | Stop-Process -Force
    $repairDamageNames = @(
        'SuavoAgent.Helper.exe',
        'SuavoAgent.Maintenance.exe'
    )
    foreach ($name in $repairDamageNames) {
        $path = Join-Path $installDirectory $name
        Remove-Item -LiteralPath $path -Force
        Assert-Condition (-not (Test-Path -LiteralPath $path)) `
            "repair:damage_injection_failed:$name"
    }

    Invoke-InstallOrRepair $true $repairLog
    $repairedMarker = Assert-InstalledState $msiSha256
    foreach ($name in $repairDamageNames) {
        Get-RegularFile (Join-Path $installDirectory $name) `
            "repair:not_restored:$name" | Out-Null
    }
    Assert-Condition ((Get-Sha256 $markerPath) -ceq $markerSha256) 'repair:marker_changed'
    Assert-Condition ([string]$repairedMarker.installTransactionId -ceq $markerTransactionId) 'repair:transaction_changed'
    $phaseResults.Add('repair-verified-without-new-install-proof')

    if ($InstallerKind -eq 'Msi') {
        Invoke-CheckedProcess 'msiexec.exe' @(
            '/x', "`"$($msiItem.FullName)`"", '/qn', '/norestart', '/L*v', "`"$uninstallLog`""
        ) @(0, 3010) 'uninstall'
    } else {
        Invoke-CheckedProcess $installerItem.FullName @(
            '/uninstall', '/quiet', '/norestart', '/log', "`"$uninstallLog`""
        ) @(0, 1641, 3010) 'uninstall'
    }
    foreach ($name in $serviceNames) {
        Assert-Condition ($null -eq (Get-Service -Name $name -ErrorAction SilentlyContinue)) "uninstall:service_remained:$name"
    }
    Assert-Condition (-not (Test-Path -LiteralPath $installDirectory)) 'uninstall:program_files_remained'
    Assert-Condition (-not (Test-Path -LiteralPath 'HKLM:\SOFTWARE\MKM Technologies LLC\SuavoAgent')) 'uninstall:registry_remained'
    Assert-Condition (Test-Path -LiteralPath $dataDirectory -PathType Container) 'uninstall:regulated_data_not_preserved'
    Get-RegularFile $retentionSentinelPath 'uninstall:retention_sentinel' | Out-Null
    Assert-Condition `
        ((Get-Sha256 $retentionSentinelPath) -ceq $retentionSentinelSha256) `
        'uninstall:retention_sentinel_changed'
    $phaseResults.Add('uninstall-verified')
} catch {
    $failureCode = $_.Exception.GetType().Name
    throw
} finally {
    $evidence = [ordered]@{
        schemaVersion = 1
        purpose = 'suavoagent-installer-rehearsal'
        phiClassification = 'phi-negative'
        installerKind = $InstallerKind.ToLowerInvariant()
        releaseTag = $ExpectedReleaseTag
        installerArtifactSha256 = $artifactSha256
        msiArtifactSha256 = $msiSha256
        installMarkerSha256 = $markerSha256
        installTransactionId = $markerTransactionId
        retainedDataSentinelSha256 = $retentionSentinelSha256
        installedCohort = $installedHashes
        phases = @($phaseResults)
        startedAtUtc = $startedAtUtc
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        passed = ($null -eq $failureCode -and $phaseResults.Count -eq 4)
        failureType = $failureCode
    }
    [IO.File]::WriteAllText(
        $evidencePath,
        ($evidence | ConvertTo-Json -Depth 6) + "`n",
        [Text.UTF8Encoding]::new($false))
}
