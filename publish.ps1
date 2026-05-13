# SuavoAgent v2 - Publish all binaries
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = ".\publish",
    [string]$CertThumbprint = "",
    [string]$TimestampServer = "http://timestamp.digicert.com",
    [switch]$GenerateKeys
)

$ErrorActionPreference = "Stop"
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"

# ── suavo-report-crash.exe helper (Diagnostic Mesh PR 4c) ──
# AOT, stdlib-only crash reporter invoked when dotnet publish fails.
# Captures exit code + project name + stdout tail + build SHA + publish
# env shape (parent PS version, Yubikey, SmartCard) into a JSONL marker
# at %ProgramData%\SuavoAgent\diagnostics\publish-crash.jsonl.
#
# Falls back to %TEMP%\suavo-crash-report-failed.<pid>.txt if the tool
# is missing or itself fails. publish.ps1 preserves the original
# $LASTEXITCODE either way (no diagnostic failure masks a publish failure).
function Invoke-SuavoReportCrash {
    param(
        [Parameter(Mandatory)][string]$Component,
        [Parameter(Mandatory)][int]$ExitCode,
        [string]$Project = "",
        [string]$StdoutTail = "",
        [string]$ToolPath = "$PSScriptRoot\publish\SuavoReportCrash\suavo-report-crash.exe",
        [string]$CertThumbprintRaw = $script:CertThumbprint
    )

    $originalExit = $ExitCode
    try {
        # Build SHA -- best-effort
        $buildSha = ""
        try { $buildSha = (& git rev-parse --short=12 HEAD 2>$null).Trim() } catch { }

        # Yubikey / SmartCard / parent-PS shape -- only meaningful on Windows
        $yubikey = $false
        $smartcard = $false
        if (Get-Command Get-PnpDevice -ErrorAction SilentlyContinue) {
            $yubikey = [bool](Get-PnpDevice -ErrorAction SilentlyContinue |
                Where-Object { $_.FriendlyName -match 'Yubikey|YubiKey' -and $_.Status -eq 'OK' } |
                Select-Object -First 1)
        }
        if (Get-Command Get-Service -ErrorAction SilentlyContinue) {
            $svc = Get-Service -Name SCardSvr -ErrorAction SilentlyContinue
            $smartcard = $svc -and $svc.Status -eq 'Running'
        }

        if (Test-Path -LiteralPath $ToolPath) {
            $argv = @(
                '--component', $Component,
                '--exit-code', "$originalExit",
                '--project', $Project,
                '--stdout-tail', $StdoutTail,
                '--build-sha', $buildSha,
                '--publish-pid', "$PID",
                '--parent-ps-version', "$($PSVersionTable.PSVersion)",
                '--yubikey', ($yubikey.ToString().ToLowerInvariant()),
                '--smartcard', ($smartcard.ToString().ToLowerInvariant())
            )
            if ($CertThumbprintRaw) {
                $argv += '--ev-cert-thumbprint'
                $argv += $CertThumbprintRaw
            }
            & $ToolPath @argv | Out-Null
        } else {
            # Tool not yet built (likely a first run before its dotnet
            # publish step) -- write a fallback marker so the failure is
            # still observable to the next agent run / operator.
            $marker = Join-Path $env:TEMP "suavo-crash-report-failed.$PID.txt"
            $detail = "tool missing at $ToolPath; originalExit=$originalExit; component=$Component; project=$Project; ts=$([DateTimeOffset]::UtcNow.ToString('o'))"
            Set-Content -Path $marker -Value $detail -Encoding UTF8 -ErrorAction SilentlyContinue
        }
    } catch {
        # Diagnostic failure must NOT mask the original publish failure.
        try {
            $marker = Join-Path $env:TEMP "suavo-crash-report-failed.$PID.txt"
            Set-Content -Path $marker `
                -Value "suavo-report-crash invocation failed: $($_.Exception.GetType().FullName): $($_.Exception.Message); originalExit=$originalExit" `
                -Encoding UTF8 -ErrorAction SilentlyContinue
        } catch { }
    }
}

# ── dotnet publish heartbeat helpers (Diagnostic Mesh PR 2) ──
# Replace the silent 8-min "dotnet publish" with per-project [start/elapsed/
# finish] visibility + 30s heartbeat. Solves the 2026-05-12 failure mode
# where Joshua Ctrl+C'd a healthy 8-min build thinking it had hung.

function Get-DotnetPublishHint {
    param([string]$StdoutPath)
    try {
        $lines = Get-Content -LiteralPath $StdoutPath -Tail 5 -ErrorAction SilentlyContinue
        $latest = $lines | Where-Object { $_.Trim() } | Select-Object -Last 1
        if ($latest) {
            $trimmed = $latest.Trim()
            if ($trimmed.Length -gt 60) { $trimmed = $trimmed.Substring(0, 57) + '...' }
            return " | still compiling ($trimmed)"
        }
    } catch {
        # transient read collisions while dotnet writes are expected; fall through
    }
    return " | still compiling..."
}

function Invoke-DotnetPublishWithHeartbeat {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProjectName,
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)][string]$Configuration,
        [Parameter(Mandatory)][string]$Runtime,
        [Parameter(Mandatory)][datetime]$BatchStart,
        [int]$HeartbeatSeconds = 30
    )

    $tag = ("[$ProjectName]").PadRight(11)
    $offsetSec = [int]((Get-Date) - $BatchStart).TotalSeconds
    $banner = "$tag start=T+${offsetSec}s"
    Write-Host "`n$banner | publishing..." -ForegroundColor Yellow

    $stdoutLog = [System.IO.Path]::GetTempFileName()
    $stderrLog = [System.IO.Path]::GetTempFileName()

    $publishArgs = @(
        'publish', $ProjectPath,
        '-c', $Configuration,
        '-r', $Runtime,
        '--self-contained',
        '-p:PublishSingleFile=true',
        '-p:PublishReadyToRun=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=embedded',
        '-p:PublishTrimmed=false',
        '-o', $OutputPath
    )

    $proc = Start-Process -FilePath 'dotnet' `
        -ArgumentList $publishArgs `
        -NoNewWindow -PassThru `
        -RedirectStandardOutput $stdoutLog `
        -RedirectStandardError $stderrLog

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $lastHeartbeat = Get-Date
    $interrupted = $false

    try {
        while (-not $proc.HasExited) {
            Start-Sleep -Milliseconds 500
            $now = Get-Date
            if (($now - $lastHeartbeat).TotalSeconds -ge $HeartbeatSeconds) {
                $elapsed = [int]$sw.Elapsed.TotalSeconds
                $hint = Get-DotnetPublishHint -StdoutPath $stdoutLog
                Write-Host "$banner | elapsed=${elapsed}s$hint" -ForegroundColor DarkGray
                $lastHeartbeat = $now
            }
        }
    } catch [System.Management.Automation.PipelineStoppedException] {
        $interrupted = $true
        throw
    } finally {
        if (-not $proc.HasExited) {
            try { $proc.Kill() } catch { }
            $interrupted = $true
        }
        if ($interrupted) {
            $elapsed = [int]$sw.Elapsed.TotalSeconds
            Write-Host "[*] BUILD INTERRUPTED at $ProjectName after ${elapsed}s. Re-run with -CleanFirst if rebuild seems incomplete." -ForegroundColor Red
        }
    }

    $sw.Stop()
    $totalElapsed = [int]$sw.Elapsed.TotalSeconds
    $exitCode = $proc.ExitCode

    if ($exitCode -ne 0) {
        Write-Host "$banner | elapsed=${totalElapsed}s | FAILED (exit=$exitCode)" -ForegroundColor Red
        $stderrTail = Get-Content -LiteralPath $stderrLog -Tail 20 -ErrorAction SilentlyContinue
        if ($stderrTail) { Write-Host ($stderrTail -join "`n") -ForegroundColor Red }
        $stdoutTail = Get-Content -LiteralPath $stdoutLog -Tail 5 -ErrorAction SilentlyContinue
        if ($stdoutTail) { Write-Host ($stdoutTail -join "`n") -ForegroundColor DarkGray }
        Remove-Item -LiteralPath $stdoutLog, $stderrLog -ErrorAction SilentlyContinue
        return [pscustomobject]@{ Success = $false; ExitCode = $exitCode; ElapsedSeconds = $totalElapsed }
    }

    $exe = Get-ChildItem $OutputPath -Filter "*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    $sizeStr = if ($exe) {
        $sizeMb = [math]::Round($exe.Length / 1MB, 1)
        " | $($exe.Name) ($sizeMb MB)"
    } else { "" }
    Write-Host "$banner | elapsed=${totalElapsed}s | DONE in ${totalElapsed}s$sizeStr" -ForegroundColor Green

    Remove-Item -LiteralPath $stdoutLog, $stderrLog -ErrorAction SilentlyContinue
    return [pscustomobject]@{ Success = $true; ExitCode = 0; ElapsedSeconds = $totalElapsed }
}

# ── Auto-generate ECDSA signing keys if missing ──
# $env:HOME is set on macOS/Linux and inside pwsh, but is NOT set on
# native Windows PowerShell 5.1. Fall back through the standard home-dir
# variables so the same script runs on Mac dev boxes and Windows agents.
$suavoHome = if ($env:HOME) { $env:HOME }
             elseif ($env:USERPROFILE) { $env:USERPROFILE }
             else { throw "publish.ps1: no HOME or USERPROFILE env var -- cannot locate .suavo key dir" }
$suavoKeyDir = Join-Path $suavoHome ".suavo"
$signingKey = Join-Path $suavoKeyDir "signing-key.pem"
$signingPub = Join-Path $suavoKeyDir "signing-key.pub.pem"
$cmdKey = Join-Path $suavoKeyDir "cmd-signing-key.pem"
$cmdPub = Join-Path $suavoKeyDir "cmd-signing-key.pub.pem"

$needKeys = $GenerateKeys -or (-not (Test-Path $signingKey)) -or (-not (Test-Path $cmdKey))
if ($needKeys) {
    Write-Host "=== Generating ECDSA P-256 signing keys ===" -ForegroundColor Yellow
    New-Item -Path $suavoKeyDir -ItemType Directory -Force | Out-Null

    if (Get-Command openssl -ErrorAction SilentlyContinue) {
        # Primary path: openssl
        if ($GenerateKeys -or (-not (Test-Path $signingKey))) {
            openssl ecparam -name prime256v1 -genkey -noout -out $signingKey
            openssl ec -in $signingKey -pubout -out $signingPub 2>$null
            Write-Host "  signing-key.pem + .pub.pem generated (openssl)" -ForegroundColor Green
        }
        if ($GenerateKeys -or (-not (Test-Path $cmdKey))) {
            openssl ecparam -name prime256v1 -genkey -noout -out $cmdKey
            openssl ec -in $cmdKey -pubout -out $cmdPub 2>$null
            Write-Host "  cmd-signing-key.pem + .pub.pem generated (openssl)" -ForegroundColor Green
        }
    } else {
        # Fallback: .NET ECDsa key generation. Requires the .NET 7+ APIs
        # `ExportECPrivateKeyPem` / `ExportSubjectPublicKeyInfoPem`, which
        # exist in PowerShell 7+ (built on .NET 6+ / 7+).
        #
        # Two observed null-return paths from the curve-named factory
        # `[ECDsa]::Create([ECCurve]::NamedCurves.nistP256)`:
        #   1. Windows PowerShell 5.1 / .NET Framework 4.x: factory doesn't
        #      understand the ECCurve overload.
        #   2. pwsh 7.6 on Queen (2026-05-12): CNG named-curve resolution
        #      returns null even with the right runtime (suspected FIPS / CNG
        #      provider config quirk).
        # Both manifest as "method on null-valued expression" on the next
        # `.ExportECPrivateKeyPem()` call, which is opaque to a rebuilder.
        #
        # Construct ECDsaCng directly with the key size: that bypasses the
        # named-curve resolution path and is what `ECDsa.Create(curve)` would
        # have returned on Windows anyway. ECDsaCng inherits the .NET 7+ PEM
        # export methods from the base ECDsa class.
        if ($PSVersionTable.PSVersion.Major -lt 7) {
            throw "publish.ps1 key-generation fallback requires PowerShell 7+ (uses .NET 7+ ECDsa PEM APIs not present in Windows PowerShell 5.1). Either install openssl, run this script with pwsh.exe, or pre-create keys at $suavoKeyDir."
        }
        Write-Host "  openssl not found - using .NET ECDsaCng key generation" -ForegroundColor Yellow
        function Export-ECDsaKeyPair([string]$privPath, [string]$pubPath) {
            $ecdsa = [System.Security.Cryptography.ECDsaCng]::new(256)
            if (-not $ecdsa) {
                throw "ECDsaCng(256) constructor returned null on PowerShell $($PSVersionTable.PSVersion). Install openssl (preferred) or pre-create keys at $suavoKeyDir."
            }
            $privPem = $ecdsa.ExportECPrivateKeyPem()
            $pubPem = $ecdsa.ExportSubjectPublicKeyInfoPem()
            Set-Content -Path $privPath -Value $privPem -Encoding UTF8 -NoNewline
            Set-Content -Path $pubPath -Value $pubPem -Encoding UTF8 -NoNewline
            $ecdsa.Dispose()
        }
        if ($GenerateKeys -or (-not (Test-Path $signingKey))) {
            Export-ECDsaKeyPair $signingKey $signingPub
            Write-Host "  signing-key.pem + .pub.pem generated (.NET)" -ForegroundColor Green
        }
        if ($GenerateKeys -or (-not (Test-Path $cmdKey))) {
            Export-ECDsaKeyPair $cmdKey $cmdPub
            Write-Host "  cmd-signing-key.pem + .pub.pem generated (.NET)" -ForegroundColor Green
        }
    }
    Write-Host "  Keys at: $suavoKeyDir" -ForegroundColor Cyan
    Write-Host ""
}

Write-Host "=== Publishing SuavoAgent v2 ===" -ForegroundColor Cyan
Write-Host "Config: $Configuration | Runtime: $Runtime" -ForegroundColor Gray

if (Test-Path $OutputDir) { Remove-Item -Recurse -Force $OutputDir }
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$projects = @(
    @{ Name = "Core";     Path = "src\SuavoAgent.Core\SuavoAgent.Core.csproj";     Exe = "SuavoAgent.Core.exe" },
    @{ Name = "Broker";   Path = "src\SuavoAgent.Broker\SuavoAgent.Broker.csproj"; Exe = "SuavoAgent.Broker.exe" },
    @{ Name = "Helper";   Path = "src\SuavoAgent.Helper\SuavoAgent.Helper.csproj"; Exe = "SuavoAgent.Helper.exe" },
    @{ Name = "Watchdog"; Path = "src\SuavoAgent.Watchdog\SuavoAgent.Watchdog.csproj"; Exe = "SuavoAgent.Watchdog.exe" },
    @{ Name = "Setup";    Path = "src\SuavoAgent.Setup\SuavoAgent.Setup.csproj";   Exe = "SuavoSetup.exe" }
)

# Build suavo-report-crash.exe FIRST so it's available to capture failures
# from the main projects' publish steps. AOT publish is best-effort: if it
# fails (e.g., cross-compile toolchain missing on Mac dev box), the
# Invoke-SuavoReportCrash helper falls back to the %TEMP% marker path.
Write-Host "`n[ReportCrash] Building AOT crash reporter..." -ForegroundColor Yellow
$reportCrashOut = Join-Path $OutputDir "SuavoReportCrash"
dotnet publish "tools\SuavoReportCrash\SuavoReportCrash.csproj" `
    -c $Configuration `
    -r $Runtime `
    --self-contained `
    -o $reportCrashOut 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ReportCrash] AOT build FAILED -- falling back to %TEMP% marker path on publish failures" -ForegroundColor Yellow
} else {
    Write-Host "[ReportCrash] OK -- suavo-report-crash.exe at $reportCrashOut" -ForegroundColor Green
}

$batchStart = Get-Date
foreach ($proj in $projects) {
    $outPath = Join-Path $OutputDir $proj.Name
    $result = Invoke-DotnetPublishWithHeartbeat `
        -ProjectName $proj.Name `
        -ProjectPath $proj.Path `
        -OutputPath $outPath `
        -Configuration $Configuration `
        -Runtime $Runtime `
        -BatchStart $batchStart
    if (-not $result.Success) {
        # Mesh PR 4c: invoke crash reporter before exit so the failure shows
        # up in publish-crash.jsonl. Heartbeat helper already streamed
        # stdout/stderr tail to console; the JSONL marker carries exit code
        # + project + env shape.
        Invoke-SuavoReportCrash `
            -Component "Publish" `
            -ExitCode $result.ExitCode `
            -Project $proj.Name
        exit $result.ExitCode
    }
}
$batchElapsed = [int]((Get-Date) - $batchStart).TotalSeconds
Write-Host "`n=== All $($projects.Count) projects published in ${batchElapsed}s ===" -ForegroundColor Cyan

Write-Host "`n=== Publish Complete ===" -ForegroundColor Cyan
Write-Host "Output: $OutputDir"
Get-ChildItem $OutputDir -Recurse -Filter "*.exe" | ForEach-Object {
    $sizeMb = [math]::Round($_.Length / 1MB, 1)
    Write-Host "  $($_.Directory.Name)\$($_.Name) - $sizeMb MB"
}

# ── Authenticode signing (if certificate thumbprint provided) ──
if ($CertThumbprint) {
    Write-Host "`n--- Authenticode Signing ---" -ForegroundColor Yellow
    $signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) {
        $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    }
    if ($signtool) {
        foreach ($proj in $projects) {
            $bin = $proj.Exe
            $path = Join-Path $OutputDir (Join-Path $proj.Name $bin)
            if (Test-Path $path) {
                Write-Host "  Signing $bin..." -ForegroundColor Gray
                & $signtool.FullName sign /sha1 $CertThumbprint /fd sha256 /tr $TimestampServer /td sha256 /v $path
                if ($LASTEXITCODE -ne 0) {
                    Write-Error "Authenticode signing FAILED for $bin"
                    exit 1
                }
                # Verify signature
                & $signtool.FullName verify /pa /v $path | Out-Null
                if ($LASTEXITCODE -eq 0) {
                    Write-Host "  $bin signed and verified" -ForegroundColor Green
                } else {
                    Write-Error "Authenticode verification FAILED for $bin"
                    exit 1
                }
            }
        }
    } else {
        Write-Error "signtool.exe not found - install Windows SDK or add to PATH"
        exit 1
    }
} else {
    Write-Host "`n--- Authenticode Signing: SKIPPED (no -CertThumbprint) ---" -ForegroundColor Yellow
    Write-Host "  To sign: .\publish.ps1 -CertThumbprint <SHA1>" -ForegroundColor Gray
}

# ── Generate checksums (AFTER signing - hash the signed binaries) ──
Write-Host "`n--- Generating checksums ---" -ForegroundColor Yellow
$checksumFile = Join-Path $OutputDir "checksums.sha256"
$checksums = @()
foreach ($proj in $projects) {
    $bin = $proj.Exe
    $path = Join-Path $OutputDir (Join-Path $proj.Name $bin)
    if (Test-Path $path) {
        $hash = (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLower()
        $checksums += "$hash  $bin"
        Write-Host "  ${bin}: $hash" -ForegroundColor Gray
    }
}
$checksums | Out-File -FilePath $checksumFile -Encoding UTF8
Write-Host "Checksums written to $checksumFile" -ForegroundColor Green

# ── Sign checksums with ECDSA (if signing key available) ──
$signingKeyPath = Join-Path $suavoHome ".suavo" "signing-key.pem"
if (Test-Path $signingKeyPath) {
    Write-Host "Signing checksums..." -ForegroundColor Yellow
    $sigFile = "$checksumFile.sig"
    $checksumBytes = [System.IO.File]::ReadAllBytes($checksumFile)
    $ecdsa = [System.Security.Cryptography.ECDsa]::Create()
    $keyPem = Get-Content $signingKeyPath -Raw
    $ecdsa.ImportFromPem($keyPem)
    $sig = $ecdsa.SignData($checksumBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    [System.Convert]::ToHexString($sig).ToLower() | Out-File -FilePath $sigFile -Encoding UTF8 -NoNewline
    Write-Host "Signature written to $sigFile" -ForegroundColor Green
} else {
    Write-Host "WARNING: No signing key at $signingKeyPath - checksums unsigned" -ForegroundColor Red
}

# ── Display command signing key info ──
$cmdKeyPath = Join-Path $suavoHome ".suavo" "cmd-signing-key.pem"
if (Test-Path $cmdKeyPath) {
    Write-Host "`n--- Command signing key ---" -ForegroundColor Yellow
    Write-Host "Key: $cmdKeyPath" -ForegroundColor Gray
    Write-Host "Use this key to sign control-plane commands (fetch_patient, decommission, update)" -ForegroundColor Gray
} else {
    Write-Host "WARNING: No command signing key at $cmdKeyPath" -ForegroundColor Red
    Write-Host "Generate with: openssl ecparam -genkey -name prime256v1 -noout | openssl ec -outform PEM > $cmdKeyPath" -ForegroundColor Red
}
