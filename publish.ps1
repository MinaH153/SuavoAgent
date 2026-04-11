# SuavoAgent v2 — Publish all binaries
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = ".\publish"
)

$ErrorActionPreference = "Stop"
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"

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

# ── Generate checksums ──
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
