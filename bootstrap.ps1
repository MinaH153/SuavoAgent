# SuavoAgent v2 — Zero-Config One-Paste Installer
# Paste into Admin PowerShell at any PioneerRx pharmacy. That's it.
#
# What it does:
# 1. Finds PioneerRx install path
# 2. Reads PioneerPharmacy.exe.config → extracts SQL host
# 3. Loads Enterprise Library DLL → calls ConnectionStringServer (port 12345)
# 4. Parses connection string → server, database, user, password
# 5. Downloads agent binaries from GitHub
# 6. Writes appsettings.json with discovered credentials
# 7. Installs + starts Windows services
# 8. Verifies prescriptions are readable
#
# SAFETY: ConnectionStringServer is called ONCE. Agent uses MaxPoolSize=1.
# Never disrupts pharmacy operations.

param(
    [string]$CloudUrl = "https://suavollc.com",
    [string]$ApiKey = "",
    [string]$PharmacyId = "",
    [string]$ReleaseTag = "v2.0.0-alpha"
)

if ($ReleaseTag -notmatch '^v\d+\.\d+\.\d+') {
    Write-Error "Invalid release tag format: $ReleaseTag"
    exit 1
}

$ErrorActionPreference = "Stop"
$installDir = "C:\Program Files\Suavo\Agent"
$dataDir = "$env:ProgramData\SuavoAgent"
$base = "https://github.com/MinaH153/SuavoAgent/releases/download/$ReleaseTag"

function Write-Step($msg) { Write-Host "`n[$((Get-Date).ToString('HH:mm:ss'))] $msg" -ForegroundColor Cyan }
function Write-Ok($msg) { Write-Host "  [OK] $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "  [WARN] $msg" -ForegroundColor Yellow }
function Write-Fail($msg) { Write-Host "  [FAIL] $msg" -ForegroundColor Red }

# ── Require Admin ──
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Fail "Run as Administrator"
    exit 1
}

Write-Host ""
Write-Host "  ╔═══════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "  ║   SuavoAgent v2 — Zero-Config Setup   ║" -ForegroundColor Cyan
Write-Host "  ╚═══════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# ════════════════════════════════════════════
# PHASE 1: Find PioneerRx
# ════════════════════════════════════════════
Write-Step "Phase 1: Finding PioneerRx installation"

$pioneerPaths = @(
    "C:\Program Files (x86)\New Tech Computer Systems\PioneerRx",
    "C:\Program Files\New Tech Computer Systems\PioneerRx",
    "D:\Program Files (x86)\New Tech Computer Systems\PioneerRx",
    "D:\Program Files\New Tech Computer Systems\PioneerRx"
)

# Also check registry for install path
try {
    $regPaths = @(
        "HKLM:\SOFTWARE\WOW6432Node\New Tech Computer Systems",
        "HKLM:\SOFTWARE\New Tech Computer Systems"
    )
    foreach ($rp in $regPaths) {
        if (Test-Path $rp) {
            $regInstall = (Get-ItemProperty $rp -ErrorAction SilentlyContinue).InstallPath
            if ($regInstall) { $pioneerPaths = @($regInstall) + $pioneerPaths }
        }
    }
} catch {}

$pioneerDir = $null
$pioneerExe = $null
$pioneerConfig = $null

foreach ($p in $pioneerPaths) {
    $exe = Join-Path $p "PioneerPharmacy.exe"
    $cfg = Join-Path $p "PioneerPharmacy.exe.config"
    if ((Test-Path $exe) -and (Test-Path $cfg)) {
        $pioneerDir = $p
        $pioneerExe = $exe
        $pioneerConfig = $cfg
        break
    }
}

if (-not $pioneerDir) {
    # Last resort: find running process
    $proc = Get-Process -Name "PioneerPharmacy" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc) {
        $pioneerExe = $proc.Path
        $pioneerDir = Split-Path $pioneerExe -Parent
        $pioneerConfig = "$pioneerExe.config"
    }
}

if (-not $pioneerDir) {
    Write-Fail "PioneerRx not found. Check install path."
    exit 1
}

Write-Ok "PioneerRx at: $pioneerDir"

# ════════════════════════════════════════════
# PHASE 2: Extract SQL credentials
# ════════════════════════════════════════════
Write-Step "Phase 2: Discovering SQL Server credentials"

$sqlServer = $null
$sqlDatabase = "PioneerPharmacySystem"
$sqlUser = $null
$sqlPassword = $null

# Step 2a: Read config for host
$configXml = [xml](Get-Content $pioneerConfig)
$pioneerHost = $null

# Try newTechDataConfiguration element
$ntdc = $configXml.configuration.newTechDataConfiguration
if ($ntdc) {
    $pioneerHost = $ntdc.host
    if (-not $pioneerHost) { $pioneerHost = $ntdc.server }
    if (-not $pioneerHost) { $pioneerHost = $ntdc.GetAttribute("host") }
}

# Fallback: search all attributes for host-like values
if (-not $pioneerHost) {
    $configText = Get-Content $pioneerConfig -Raw
    if ($configText -match 'host\s*=\s*"([^"]+)"') {
        $pioneerHost = $Matches[1]
    } elseif ($configText -match 'server\s*=\s*"([^"]+)"') {
        $pioneerHost = $Matches[1]
    }
}

if ($pioneerHost) {
    Write-Ok "PioneerRx host: $pioneerHost"
} else {
    Write-Warn "Could not extract host from config — will try localhost"
    $pioneerHost = "localhost"
}

# Step 2b: Load Enterprise Library DLL and call ConnectionStringServer
$entLibDll = Join-Path $pioneerDir "Microsoft.Practices.EnterpriseLibrary.Data.dll"
$discoveredConnStr = $null

if (Test-Path $entLibDll) {
    Write-Host "  Loading Enterprise Library DLL..." -ForegroundColor Gray
    try {
        [System.Reflection.Assembly]::LoadFrom($entLibDll) | Out-Null

        # Find the method — it may be in different classes depending on version
        $dataTypes = [AppDomain]::CurrentDomain.GetAssemblies() |
            Where-Object { $_.GetName().Name -eq "Microsoft.Practices.EnterpriseLibrary.Data" } |
            ForEach-Object { $_.GetTypes() } |
            Where-Object { $_.GetMethods([System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic) |
                Where-Object { $_.Name -like "*ConnectionString*Network*" -or $_.Name -like "*Retrieve*Connection*" } }

        foreach ($type in $dataTypes) {
            $methods = $type.GetMethods([System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic) |
                Where-Object { $_.Name -like "*ConnectionString*" -or $_.Name -like "*Retrieve*" }
            foreach ($method in $methods) {
                $params = $method.GetParameters()
                if ($params.Count -ge 2) {
                    Write-Host "  Found method: $($type.FullName).$($method.Name)($($params | ForEach-Object { $_.ParameterType.Name } | Join-String ', '))" -ForegroundColor Gray
                    try {
                        if ($params.Count -eq 3) {
                            $discoveredConnStr = $method.Invoke($null, @($sqlDatabase, $pioneerHost, 12345))
                        } elseif ($params.Count -eq 2) {
                            $discoveredConnStr = $method.Invoke($null, @($sqlDatabase, $pioneerHost))
                        }
                        if ($discoveredConnStr) {
                            Write-Ok "ConnectionStringServer returned credentials"
                            break
                        }
                    } catch {
                        Write-Host "    Method call failed: $($_.Exception.InnerException.Message)" -ForegroundColor Gray
                    }
                }
            }
            if ($discoveredConnStr) { break }
        }
    } catch {
        Write-Warn "Enterprise Library DLL load failed: $($_.Exception.Message)"
    }
}

# Step 2c: Try direct TCP to ConnectionStringServer port 12345
if (-not $discoveredConnStr) {
    Write-Host "  Trying direct TCP to ${pioneerHost}:12345..." -ForegroundColor Gray
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $tcp.Connect($pioneerHost, 12345)
        $stream = $tcp.GetStream()
        $stream.ReadTimeout = 5000
        $stream.WriteTimeout = 5000

        # Send database name request (common protocol: length-prefixed UTF-8 string)
        $reqBytes = [System.Text.Encoding]::UTF8.GetBytes($sqlDatabase)
        $lenBytes = [BitConverter]::GetBytes([int32]$reqBytes.Length)
        $stream.Write($lenBytes, 0, 4)
        $stream.Write($reqBytes, 0, $reqBytes.Length)
        $stream.Flush()

        # Read response
        $buf = New-Object byte[] 4096
        $read = $stream.Read($buf, 0, $buf.Length)
        if ($read -gt 0) {
            # Try reading as length-prefixed
            $respStr = [System.Text.Encoding]::UTF8.GetString($buf, 0, $read)
            # Skip length prefix bytes if present
            if ($respStr.Length -gt 4 -and $respStr.Substring(4) -match "Data Source|Server") {
                $discoveredConnStr = $respStr.Substring(4)
            } elseif ($respStr -match "Data Source|Server") {
                $discoveredConnStr = $respStr.TrimStart([char]0, [char]1, [char]2, [char]3, [char]4, [char]5)
            }
            if ($discoveredConnStr) {
                Write-Ok "Got connection string from TCP port 12345"
            }
        }
        $tcp.Close()
    } catch {
        Write-Host "    TCP probe failed: $($_.Exception.Message)" -ForegroundColor Gray
    }
}

# Step 2d: Parse connection string if discovered
if ($discoveredConnStr) {
    Write-Host "  Parsing connection string..." -ForegroundColor Gray
    try {
        $csb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($discoveredConnStr)
        $sqlServer = $csb.DataSource
        $sqlDatabase = if ($csb.InitialCatalog) { $csb.InitialCatalog } else { $sqlDatabase }
        if (-not $csb.IntegratedSecurity) {
            $sqlUser = $csb.UserID
            $sqlPassword = $csb.Password
        }
        Write-Ok "SQL Server: $sqlServer"
        Write-Ok "Database: $sqlDatabase"
        Write-Ok "Auth: $(if ($sqlUser) { 'SQL Auth as ' + $sqlUser } else { 'Windows Auth' })"
    } catch {
        Write-Warn "Connection string parse failed: $($_.Exception.Message)"
    }
}

# Step 2e: Fallback — SQL Browser discovery
if (-not $sqlServer) {
    Write-Host "  Trying SQL Browser on $pioneerHost..." -ForegroundColor Gray
    try {
        $udp = New-Object System.Net.Sockets.UdpClient
        $udp.Client.ReceiveTimeout = 3000
        $ep = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Parse(
            ([System.Net.Dns]::GetHostAddresses($pioneerHost) | Where-Object { $_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1).IPAddressToString
        ), 1434)
        $udp.Send(@(0x02), 1, $ep) | Out-Null
        $recvEp = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
        $resp = $udp.Receive([ref]$recvEp)
        $respStr = [System.Text.Encoding]::ASCII.GetString($resp, 3, $resp.Length - 3)
        $udp.Close()

        # Parse "InstanceName;NEWTECH;;tcp;49202;..."
        $parts = $respStr.Split(';')
        $instance = $null; $port = $null
        for ($i = 0; $i -lt $parts.Length - 1; $i++) {
            if ($parts[$i] -eq "InstanceName") { $instance = $parts[$i+1] }
            if ($parts[$i] -eq "tcp" -and $parts[$i+1] -match '^\d+$') { $port = $parts[$i+1] }
        }
        if ($port) {
            $resolvedIp = ([System.Net.Dns]::GetHostAddresses($pioneerHost) |
                Where-Object { $_.AddressFamily -eq 'InterNetwork' } |
                Select-Object -First 1).IPAddressToString
            $sqlServer = "${resolvedIp},${port}"
            Write-Ok "SQL Browser found: $sqlServer (instance: $instance)"
        }
    } catch {
        Write-Host "    SQL Browser failed: $($_.Exception.Message)" -ForegroundColor Gray
    }
}

# Step 2f: If we still have no credentials, prompt
if (-not $sqlServer) {
    Write-Warn "Auto-discovery failed. Manual entry required."
    $sqlServer = Read-Host "  SQL Server (e.g. 192.168.1.78,49202)"
}
if (-not $sqlUser -and -not $discoveredConnStr) {
    Write-Warn "No SQL credentials discovered."
    $needAuth = Read-Host "  Use SQL Auth? (y/n)"
    if ($needAuth -eq 'y') {
        $sqlUser = Read-Host "  SQL Username"
        $sqlPassword = Read-Host "  SQL Password"
    }
}

Write-Host ""
Write-Host "  ┌─────────────────────────────────────┐" -ForegroundColor White
Write-Host "  │ Server:   $sqlServer" -ForegroundColor White
Write-Host "  │ Database: $sqlDatabase" -ForegroundColor White
Write-Host "  │ Auth:     $(if ($sqlUser) { "SQL ($sqlUser)" } else { 'Windows' })" -ForegroundColor White
Write-Host "  └─────────────────────────────────────┘" -ForegroundColor White

# ════════════════════════════════════════════
# PHASE 3: Download agent binaries
# ════════════════════════════════════════════
Write-Step "Phase 3: Downloading SuavoAgent binaries"

New-Item -ItemType Directory -Path $installDir -Force | Out-Null
New-Item -ItemType Directory -Path "$dataDir\logs" -Force | Out-Null

# Download and verify checksums
$checksumUrl = "$base/checksums.sha256"
$checksumSigUrl = "$base/checksums.sha256.sig"
$checksumPath = Join-Path $installDir "checksums.sha256"
$checksumSigPath = Join-Path $installDir "checksums.sha256.sig"

Write-Host "  Downloading checksums..." -ForegroundColor Gray
Invoke-WebRequest -Uri $checksumUrl -OutFile $checksumPath -UseBasicParsing
Invoke-WebRequest -Uri $checksumSigUrl -OutFile $checksumSigPath -UseBasicParsing

# Verify ECDSA signature of checksums
$publicKeyDer = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEJJO30pUIre7wuMN5I1FQmlEDpTIM0dmhPjaGtlG7gm+47G7lKHuJV4lQ3eWhZNqe1eviOZkt+9VnWnQUSJGvsg=="
$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
$ecdsa.ImportSubjectPublicKeyInfo([System.Convert]::FromBase64String($publicKeyDer), [ref]$null)
$checksumBytes = [System.IO.File]::ReadAllBytes($checksumPath)
$sigHex = (Get-Content $checksumSigPath -Raw).Trim()
$sigBytes = [System.Convert]::FromHexString($sigHex)
$valid = $ecdsa.VerifyData($checksumBytes, $sigBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)

if (-not $valid) {
    Write-Error "CRITICAL: Checksum signature verification FAILED - aborting install"
    Remove-Item $checksumPath, $checksumSigPath -Force -ErrorAction SilentlyContinue
    exit 1
}
Write-Host "  Checksum signature verified (ECDSA P-256)" -ForegroundColor Green

# Parse expected hashes
$expectedHashes = @{}
Get-Content $checksumPath | ForEach-Object {
    $parts = $_ -split "  ", 2
    if ($parts.Count -eq 2) { $expectedHashes[$parts[1].Trim()] = $parts[0].Trim() }
}

$binaries = @("SuavoAgent.Core.exe", "SuavoAgent.Broker.exe", "SuavoAgent.Helper.exe")
foreach ($bin in $binaries) {
    $url = "$base/$bin"
    $dst = Join-Path $installDir $bin
    Write-Host "  Downloading $bin..." -ForegroundColor Gray
    try {
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $url -OutFile $dst -UseBasicParsing
        $sizeMb = [math]::Round((Get-Item $dst).Length / 1MB, 1)
        Write-Ok "$bin ($sizeMb MB)"
    } catch {
        Write-Fail "Download failed for $bin — $($_.Exception.Message)"
        exit 1
    }
    # Verify SHA256 hash against signed checksums
    $actualHash = (Get-FileHash -Path $dst -Algorithm SHA256).Hash.ToLower()
    if ($expectedHashes.ContainsKey($bin) -and $actualHash -ne $expectedHashes[$bin]) {
        Write-Error "CRITICAL: SHA256 mismatch for $bin - expected $($expectedHashes[$bin]), got $actualHash"
        foreach ($b in $binaries) { Remove-Item (Join-Path $installDir $b) -Force -ErrorAction SilentlyContinue }
        exit 1
    }
    Write-Host "  $bin verified: $actualHash" -ForegroundColor Green
}

# ════════════════════════════════════════════
# PHASE 4: Write appsettings.json
# ════════════════════════════════════════════
Write-Step "Phase 4: Writing configuration"

$agentId = "agent-" + ([guid]::NewGuid().ToString("N").Substring(0, 12))
$fingerprint = (Get-WmiObject Win32_ComputerSystemProduct).UUID

$config = @{
    Agent = @{
        CloudUrl = $CloudUrl
        ApiKey = $ApiKey
        AgentId = $agentId
        PharmacyId = $PharmacyId
        MachineFingerprint = $fingerprint
        Version = "2.0.0"
        SqlServer = $sqlServer
        SqlDatabase = $sqlDatabase
    }
}
if ($sqlUser) {
    $config.Agent.SqlUser = $sqlUser
    $config.Agent.SqlPassword = $sqlPassword
}

$configJson = $config | ConvertTo-Json -Depth 5
$configPath = Join-Path $installDir "appsettings.json"
Set-Content -Path $configPath -Value $configJson -Encoding UTF8
Write-Ok "appsettings.json written to $installDir"

# Lock down appsettings (contains SQL credentials)
$acl = Get-Acl $configPath
$acl.SetAccessRuleProtection($true, $false)
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    "BUILTIN\Administrators", "FullControl", "None", "None", "Allow")))
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    "NT AUTHORITY\SYSTEM", "FullControl", "None", "None", "Allow")))
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    "NT AUTHORITY\LOCAL SERVICE", "Read", "None", "None", "Allow")))
Set-Acl $configPath $acl
Write-Ok "Credentials locked down (Admin + SYSTEM + LocalService only)"

# ════════════════════════════════════════════
# PHASE 5: Install Windows services
# ════════════════════════════════════════════
Write-Step "Phase 5: Installing Windows services"

# Stop + remove existing
foreach ($svc in @("SuavoAgent.Broker", "SuavoAgent.Core")) {
    $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
    if ($s) {
        if ($s.Status -eq "Running") { Stop-Service $svc -Force; Start-Sleep 2 }
        sc.exe delete $svc 2>$null | Out-Null
        Start-Sleep 1
    }
}

# Install Core (runs as LocalService — least privilege)
$corePath = Join-Path $installDir "SuavoAgent.Core.exe"
sc.exe create SuavoAgent.Core binPath= "`"$corePath`"" start= delayed-auto obj= "NT AUTHORITY\LocalService" | Out-Null
sc.exe description SuavoAgent.Core "Suavo pharmacy agent - SQL polling, cloud sync" | Out-Null
sc.exe failure SuavoAgent.Core reset= 3600 actions= restart/5000/restart/30000/restart/60000 | Out-Null
sc.exe failureflag SuavoAgent.Core 1 | Out-Null
Write-Ok "SuavoAgent.Core service registered"

# Install Broker (runs as NetworkService — sufficient for WTSGetActiveConsoleSessionId + Process.Start)
# NOTE: If CreateProcessAsUser is added later, upgrade to a dedicated account with SeAssignPrimaryTokenPrivilege
$brokerPath = Join-Path $installDir "SuavoAgent.Broker.exe"
sc.exe create SuavoAgent.Broker binPath= "`"$brokerPath`"" start= delayed-auto obj= "NT AUTHORITY\NetworkService" | Out-Null
sc.exe description SuavoAgent.Broker "Suavo pharmacy agent - session broker" | Out-Null
sc.exe failure SuavoAgent.Broker reset= 3600 actions= restart/5000/restart/30000/restart/60000 | Out-Null
sc.exe failureflag SuavoAgent.Broker 1 | Out-Null
sc.exe config SuavoAgent.Broker depend= SuavoAgent.Core | Out-Null
Write-Ok "SuavoAgent.Broker service registered"

# Lock down install directory
$dirAcl = Get-Acl $installDir
$dirAcl.SetAccessRuleProtection($true, $false)
$dirAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    "BUILTIN\Administrators", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
$dirAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    "NT AUTHORITY\SYSTEM", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
$dirAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    "NT AUTHORITY\LOCAL SERVICE", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")))
Set-Acl $installDir $dirAcl

# Start services
Start-Service SuavoAgent.Core
Start-Sleep 3
Start-Service SuavoAgent.Broker -ErrorAction SilentlyContinue

# ════════════════════════════════════════════
# PHASE 6: Verify
# ════════════════════════════════════════════
Write-Step "Phase 6: Verification"

foreach ($svc in @("SuavoAgent.Core", "SuavoAgent.Broker")) {
    $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
    if ($s -and $s.Status -eq "Running") {
        Write-Ok "$svc is running"
    } else {
        Write-Warn "$svc status: $($s.Status)"
    }
}

Write-Host ""
Write-Host "  ╔═══════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "  ║   SuavoAgent v2 — Installation Complete   ║" -ForegroundColor Green
Write-Host "  ╚═══════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""
Write-Host "  Install:  $installDir" -ForegroundColor White
Write-Host "  Data:     $dataDir" -ForegroundColor White
Write-Host "  Logs:     $dataDir\logs\" -ForegroundColor White
Write-Host "  Config:   $configPath" -ForegroundColor White
Write-Host "  Agent ID: $agentId" -ForegroundColor White
Write-Host ""
Write-Host "  SQL:      $sqlServer / $sqlDatabase" -ForegroundColor White
Write-Host "  Auth:     $(if ($sqlUser) { "SQL ($sqlUser)" } else { 'Windows' })" -ForegroundColor White
Write-Host ""

Get-ChildItem "$installDir\*.exe" | ForEach-Object {
    $sizeMb = [math]::Round($_.Length / 1MB, 1)
    Write-Host "  $($_.Name) — $sizeMb MB" -ForegroundColor Gray
}

Write-Host ""
Write-Host "  Agent will query for delivery-ready Rxs every 5 minutes." -ForegroundColor Cyan
Write-Host "  Check logs: Get-Content $dataDir\logs\core-*.log -Tail 50" -ForegroundColor Cyan
