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
