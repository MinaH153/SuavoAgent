# SuavoAgent Quick Install - downloads and installs, no PMS discovery
$dir = "C:\Program Files\Suavo\Agent"
$data = "$env:ProgramData\SuavoAgent"
$tag = "v3.0.0"
$base = "https://github.com/MinaH153/SuavoAgent/releases/download/$tag"

# Stop and kill any existing agent processes FIRST
Write-Host "Cleaning up existing install..." -ForegroundColor Yellow
foreach ($s in @("SuavoAgent.Broker","SuavoAgent.Core")) {
    Stop-Service $s -Force -ErrorAction SilentlyContinue
}
Start-Sleep 2
foreach ($p in @("SuavoAgent.Core","SuavoAgent.Broker","SuavoAgent.Helper")) {
    taskkill /F /IM "$p.exe" 2>$null | Out-Null
}
Start-Sleep 2
foreach ($s in @("SuavoAgent.Broker","SuavoAgent.Core")) {
    sc.exe delete $s 2>$null | Out-Null
}
Start-Sleep 1

New-Item $dir -ItemType Directory -Force | Out-Null
New-Item "$data\logs" -ItemType Directory -Force | Out-Null

Write-Host "Downloading SuavoAgent v3..." -ForegroundColor Cyan
foreach ($f in @("SuavoAgent.Core.exe","SuavoAgent.Broker.exe","SuavoAgent.Helper.exe")) {
    Write-Host "  $f..." -NoNewline
    Invoke-WebRequest "$base/$f" -OutFile "$dir\$f" -UseBasicParsing
    Write-Host " OK" -ForegroundColor Green
}

$id = "agent-" + [guid]::NewGuid().ToString("N").Substring(0,12)
$cfg = @{Agent=@{CloudUrl="https://suavollc.com";AgentId=$id;PharmacyId="TEST-001";Version="3.0.0";LearningMode=$true}} | ConvertTo-Json -Depth 5
Set-Content "$dir\appsettings.json" $cfg -Encoding ASCII

sc.exe create SuavoAgent.Core binPath= "`"$dir\SuavoAgent.Core.exe`"" start= delayed-auto obj= "NT AUTHORITY\LocalService" | Out-Null
sc.exe failure SuavoAgent.Core reset= 3600 actions= restart/5000/restart/30000/restart/60000 | Out-Null
sc.exe create SuavoAgent.Broker binPath= "`"$dir\SuavoAgent.Broker.exe`"" start= delayed-auto obj= LocalSystem | Out-Null
sc.exe failure SuavoAgent.Broker reset= 3600 actions= restart/5000/restart/30000/restart/60000 | Out-Null
sc.exe config SuavoAgent.Broker depend= SuavoAgent.Core | Out-Null

Start-Service SuavoAgent.Core
Start-Sleep 3
Start-Service SuavoAgent.Broker -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Done! Agent ID: $id" -ForegroundColor Green
Get-Service SuavoAgent.* | Format-Table Name, Status -AutoSize
Write-Host "Logs: $data\logs\" -ForegroundColor Gray
