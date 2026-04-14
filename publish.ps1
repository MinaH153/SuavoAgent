# SuavoAgent v2 — Publish all binaries
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

# ── Auto-generate ECDSA signing keys if missing ──
$suavoKeyDir = Join-Path $env:HOME ".suavo"
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
        # Fallback: .NET ECDsa (works on any machine with .NET runtime)
        Write-Host "  openssl not found — using .NET ECDsa key generation" -ForegroundColor Yellow
        function Export-ECDsaKeyPair([string]$privPath, [string]$pubPath) {
            $ecdsa = [System.Security.Cryptography.ECDsa]::Create(
                [System.Security.Cryptography.ECCurve]::NamedCurves.nistP256)
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
    @{ Name = "Core";   Path = "src\SuavoAgent.Core\SuavoAgent.Core.csproj" },
    @{ Name = "Broker"; Path = "src\SuavoAgent.Broker\SuavoAgent.Broker.csproj" },
    @{ Name = "Helper"; Path = "src\SuavoAgent.Helper\SuavoAgent.Helper.csproj" }
)

foreach ($proj in $projects) {
    Write-Host "`n[$($proj.Name)] Publishing..." -ForegroundColor Yellow
    $outPath = Join-Path $OutputDir $proj.Name

    dotnet publish $proj.Path `
        -c $Configuration `
        -r $Runtime `
        --self-contained `
        -p:PublishSingleFile=true `
        -p:PublishReadyToRun=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=embedded `
        -p:PublishTrimmed=false `
        -o $outPath

    if ($LASTEXITCODE -ne 0) {
        Write-Host "[$($proj.Name)] PUBLISH FAILED" -ForegroundColor Red
        exit 1
    }

    $exe = Get-ChildItem $outPath -Filter "*.exe" | Select-Object -First 1
    if ($exe) {
        $sizeMb = [math]::Round($exe.Length / 1MB, 1)
        Write-Host "[$($proj.Name)] OK - $($exe.Name) ($sizeMb MB)" -ForegroundColor Green
    }
}

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
        foreach ($bin in @("SuavoAgent.Core.exe", "SuavoAgent.Broker.exe", "SuavoAgent.Helper.exe")) {
            $subdir = $bin -replace "^SuavoAgent\.(.+)\.exe$", '$1'
            $path = Join-Path $OutputDir (Join-Path $subdir $bin)
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
        Write-Error "signtool.exe not found — install Windows SDK or add to PATH"
        exit 1
    }
} else {
    Write-Host "`n--- Authenticode Signing: SKIPPED (no -CertThumbprint) ---" -ForegroundColor Yellow
    Write-Host "  To sign: .\publish.ps1 -CertThumbprint <SHA1>" -ForegroundColor Gray
}

# ── Generate checksums (AFTER signing — hash the signed binaries) ──
Write-Host "`n--- Generating checksums ---" -ForegroundColor Yellow
$checksumFile = Join-Path $OutputDir "checksums.sha256"
$checksums = @()
foreach ($bin in @("SuavoAgent.Core.exe", "SuavoAgent.Broker.exe", "SuavoAgent.Helper.exe")) {
    # EXEs are in subdirectories: Core/, Broker/, Helper/
    $subdir = $bin -replace "^SuavoAgent\.(.+)\.exe$", '$1'
    $path = Join-Path $OutputDir (Join-Path $subdir $bin)
    if (Test-Path $path) {
        $hash = (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLower()
        $checksums += "$hash  $bin"
        Write-Host "  $bin: $hash" -ForegroundColor Gray
    }
}
$checksums | Out-File -FilePath $checksumFile -Encoding UTF8
Write-Host "Checksums written to $checksumFile" -ForegroundColor Green

# ── Sign checksums with ECDSA (if signing key available) ──
$signingKeyPath = Join-Path $env:HOME ".suavo" "signing-key.pem"
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
$cmdKeyPath = Join-Path $env:HOME ".suavo" "cmd-signing-key.pem"
if (Test-Path $cmdKeyPath) {
    Write-Host "`n--- Command signing key ---" -ForegroundColor Yellow
    Write-Host "Key: $cmdKeyPath" -ForegroundColor Gray
    Write-Host "Use this key to sign control-plane commands (fetch_patient, decommission, update)" -ForegroundColor Gray
} else {
    Write-Host "WARNING: No command signing key at $cmdKeyPath" -ForegroundColor Red
    Write-Host "Generate with: openssl ecparam -genkey -name prime256v1 -noout | openssl ec -outform PEM > $cmdKeyPath" -ForegroundColor Red
}
