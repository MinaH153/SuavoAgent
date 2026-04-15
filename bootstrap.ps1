# SuavoAgent v3 "Phantom" -- Zero-Config One-Paste Installer
#
# INSTALL (paste into Admin PowerShell):
#   Set-ExecutionPolicy Bypass -Scope Process -Force; irm https://raw.githubusercontent.com/MinaH153/SuavoAgent/main/bootstrap.ps1 -OutFile $env:TEMP\bs.ps1; & $env:TEMP\bs.ps1
#
# What it does:
# 1. Detects pharmacy management system (PioneerRx, or none)
# 2. Discovers SQL credentials if PMS found
# 3. Downloads signed agent binaries from GitHub
# 4. Writes appsettings.json with discovered credentials
# 5. Installs + starts Windows services
# 6. Agent auto-discovers PMS during learning phase if not detected at install
#
# SAFETY: ConnectionStringServer is called ONCE. Agent uses MaxPoolSize=1.
# Never disrupts pharmacy operations.

param(
    [string]$CloudUrl = "https://suavollc.com",
    [string]$ApiKey = "",
    [string]$PharmacyId = "",
    [string]$ReleaseTag = "v3.0.0",
    [string]$RepoOwner = "MinaH153",
    [string]$RepoName = "SuavoAgent",
    [switch]$LearningMode
)

if ($ReleaseTag -notmatch '^v\d+\.\d+\.\d+') {
    Write-Error "Invalid release tag format: $ReleaseTag"
    exit 1
}

# Auto-log everything -- transcript saved to desktop for debugging
$transcriptPath = "$env:USERPROFILE\Desktop\suavo-install-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
Start-Transcript -Path $transcriptPath -Force | Out-Null

$ErrorActionPreference = "Stop"
$installDir = "C:\Program Files\Suavo\Agent"
$dataDir = "$env:ProgramData\SuavoAgent"
$base = "https://github.com/$RepoOwner/$RepoName/releases/download/$ReleaseTag"

function Write-Step($msg) { Write-Host "`n[$((Get-Date).ToString('HH:mm:ss'))] $msg" -ForegroundColor Cyan }
function Write-Ok($msg) { Write-Host "  [OK] $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "  [WARN] $msg" -ForegroundColor Yellow }
function Write-Fail($msg) { Write-Host "  [FAIL] $msg" -ForegroundColor Red }

# -- Require Admin --
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Fail "Run as Administrator"
    exit 1
}

# -- Learning mode prompt (if not explicitly set via flag) --
if (-not $PSBoundParameters.ContainsKey('LearningMode')) {
    $lmResponse = Read-Host "Enable learning mode? (30-day observation before automation) [y/N]"
    if ($lmResponse -eq 'y' -or $lmResponse -eq 'Y') {
        $LearningMode = [switch]::new($true)
    }
}

Write-Host ""
Write-Host "  +=======================================+" -ForegroundColor Cyan
Write-Host "  |   SuavoAgent v3 -- Zero-Config Setup   |" -ForegroundColor Cyan
Write-Host "  +=======================================+" -ForegroundColor Cyan
Write-Host ""

# ============================================
# TERMS OF SERVICE + EMPLOYEE MONITORING CONSENT
# ============================================
# This consent is recorded digitally and uploaded to the Suavo cloud
# on first heartbeat. No separate paperwork needed.
Write-Host ""
Write-Host "  ╔══════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "  ║         SUAVOAGENT TERMS & CONSENT                  ║" -ForegroundColor Cyan
Write-Host "  ╚══════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "  SuavoAgent is workplace monitoring software that observes" -ForegroundColor White
Write-Host "  business workstation activity to optimize delivery operations." -ForegroundColor White
Write-Host ""
Write-Host "  WHAT IT COLLECTS:" -ForegroundColor Yellow
Write-Host "    - Application usage patterns and durations" -ForegroundColor Gray
Write-Host "    - Workstation hardware profile" -ForegroundColor Gray
Write-Host "    - Login/logout timing (shift patterns)" -ForegroundColor Gray
Write-Host "    - Website domain categories (NOT specific URLs)" -ForegroundColor Gray
Write-Host "    - Print event counts (NOT document content)" -ForegroundColor Gray
Write-Host ""
Write-Host "  WHAT IT NEVER COLLECTS:" -ForegroundColor Green
Write-Host "    - Keystrokes, passwords, or typed text" -ForegroundColor Gray
Write-Host "    - Screen captures or screenshots" -ForegroundColor Gray
Write-Host "    - Email, message, or chat content" -ForegroundColor Gray
Write-Host "    - Personal browsing history or specific URLs" -ForegroundColor Gray
Write-Host "    - File contents or document data" -ForegroundColor Gray
Write-Host ""
Write-Host "  LEGAL REQUIREMENTS:" -ForegroundColor Red
Write-Host "    CT, DE, NY: Written notice to each employee REQUIRED by law" -ForegroundColor Red
Write-Host "    CA: CCPA notice at collection required" -ForegroundColor Red
Write-Host "    All states: Employee notification is recommended best practice" -ForegroundColor Yellow
Write-Host ""
Write-Host "  BY PROCEEDING, THE AUTHORIZING PARTY CONFIRMS:" -ForegroundColor Yellow
Write-Host "    1. Authorization to install monitoring software on this machine" -ForegroundColor White
Write-Host "    2. Responsibility to notify all employees per applicable state laws" -ForegroundColor White
Write-Host "    3. Agreement to Suavo's Terms of Service and Privacy Policy" -ForegroundColor White
Write-Host "    4. Execution of Business Associate Agreement (if healthcare)" -ForegroundColor White
Write-Host ""

# Collect authorizing party info — this gets recorded in the consent receipt
$authName = Read-Host "  Authorizing party full name"
if ([string]::IsNullOrWhiteSpace($authName)) {
    Write-Host "  Installation cancelled — authorizing party name required." -ForegroundColor Red
    exit 0
}
$authTitle = Read-Host "  Title (e.g., Owner, Pharmacy Manager)"
if ([string]::IsNullOrWhiteSpace($authTitle)) { $authTitle = "Authorized Representative" }

$confirmState = Read-Host "  State where this business operates (e.g., CA, NY, TX)"
if ([string]::IsNullOrWhiteSpace($confirmState)) { $confirmState = "Unknown" }

# Check if mandatory notice state
$mandatoryNoticeStates = @("CT","DE","NY")
$highRiskStates = @("CA","IL","MA","MD","CO","MT")
$stateUpper = $confirmState.ToUpper().Trim()
if ($mandatoryNoticeStates -contains $stateUpper) {
    Write-Host ""
    Write-Host "  *** $stateUpper REQUIRES written employee notice before monitoring ***" -ForegroundColor Red
    Write-Host "  You MUST distribute the employee notice template to all staff" -ForegroundColor Red
    Write-Host "  at this workstation BEFORE SuavoAgent begins collecting data." -ForegroundColor Red
    Write-Host ""
    $noticeConfirm = Read-Host "  Type CONFIRMED to acknowledge this legal requirement"
    if ($noticeConfirm -ne "CONFIRMED") {
        Write-Host "  Installation cancelled — employee notice acknowledgment required in $stateUpper." -ForegroundColor Red
        exit 0
    }
} elseif ($highRiskStates -contains $stateUpper) {
    Write-Host ""
    Write-Host "  NOTE: $stateUpper has strong privacy protections. Employee notice is" -ForegroundColor Yellow
    Write-Host "  strongly recommended even if not strictly required by statute." -ForegroundColor Yellow
    Write-Host ""
}

Write-Host ""
$finalConfirm = Read-Host "  Type AGREE to accept terms and proceed with installation"
if ($finalConfirm -ne "AGREE") {
    Write-Host "  Installation cancelled." -ForegroundColor Red
    exit 0
}

# Build consent receipt — saved locally and uploaded on first heartbeat
$consentTimestamp = (Get-Date).ToString("o")
$consentReceipt = @{
    consentVersion = "1.0"
    authorizingParty = @{
        name = $authName
        title = $authTitle
    }
    businessState = $stateUpper
    mandatoryNoticeState = ($mandatoryNoticeStates -contains $stateUpper)
    consentTimestamp = $consentTimestamp
    termsAccepted = $true
    employeeNoticeAcknowledged = ($mandatoryNoticeStates -contains $stateUpper)
    installerVersion = "3.8.0"
    machineFingerprint = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Cryptography').MachineGuid
}
Write-Ok "Consent recorded: $authName ($authTitle) at $consentTimestamp"
Write-Host ""

# ============================================
# PHASE 1: Detect Pharmacy Management System
# ============================================
Write-Step "Phase 1: Detecting pharmacy management system"

$pmsType = $null
$pioneerDir = $null
$pioneerExe = $null
$pioneerConfig = $null

# --- PioneerRx detection ---
$pioneerPaths = @(
    "C:\Program Files (x86)\New Tech Computer Systems\PioneerRx",
    "C:\Program Files\New Tech Computer Systems\PioneerRx",
    "D:\Program Files (x86)\New Tech Computer Systems\PioneerRx",
    "D:\Program Files\New Tech Computer Systems\PioneerRx"
)
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
foreach ($p in $pioneerPaths) {
    $exe = Join-Path $p "PioneerPharmacy.exe"
    $cfg = Join-Path $p "PioneerPharmacy.exe.config"
    if ((Test-Path $exe) -and (Test-Path $cfg)) {
        $pioneerDir = $p; $pioneerExe = $exe; $pioneerConfig = $cfg; $pmsType = "PioneerRx"
        break
    }
}
if (-not $pmsType) {
    $proc = Get-Process -Name "PioneerPharmacy" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc) {
        $pioneerExe = $proc.Path; $pioneerDir = Split-Path $pioneerExe -Parent
        $pioneerConfig = "$pioneerExe.config"; $pmsType = "PioneerRx"
    }
}

# --- Future PMS detections go here ---
# QS/1: Check for "QS1.exe" or registry
# Liberty: Check for "Liberty.exe" or known paths
# McKesson: Check for "EnterpriseRx" paths
# Each sets $pmsType and relevant config vars

# --- Result ---
if ($pmsType) {
    Write-Ok "Detected: $pmsType at $pioneerDir"
} else {
    Write-Warn "No pharmacy management system detected on this machine"
    Write-Host "  The agent will install and auto-discover the PMS during its learning phase." -ForegroundColor Gray
    Write-Host "  Supported: PioneerRx (more adapters coming)" -ForegroundColor Gray
    Write-Host ""
}

# ============================================
# PHASE 2: Extract SQL credentials
# ============================================
# Pause transcript during credential discovery — SQL passwords must not be logged
try { Stop-Transcript -ErrorAction SilentlyContinue | Out-Null } catch { }

if (-not $pmsType) {
    Write-Step "Phase 2: Skipping SQL discovery (no PMS detected)"
    $sqlServer = ""
    $sqlDatabase = ""
    $sqlUser = $null
    $sqlPassword = $null
    Write-Host "  Agent will discover SQL during learning phase" -ForegroundColor Gray
} else {
    Write-Step "Phase 2: Discovering SQL Server credentials"
}

$sqlServer = if (-not $pmsType) { "" } else { $null }
$sqlDatabase = "PioneerPharmacySystem"
$sqlUser = $null
$sqlPassword = $null

if ($pmsType) {
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
    Write-Warn "Could not extract host from config -- will try localhost"
    $pioneerHost = "localhost"
}

# Step 2b: Load Enterprise Library DLL and call ConnectionStringServer
$entLibDll = Join-Path $pioneerDir "Microsoft.Practices.EnterpriseLibrary.Data.dll"
$discoveredConnStr = $null

if (Test-Path $entLibDll) {
    Write-Host "  Loading Enterprise Library DLL..." -ForegroundColor Gray
    try {
        [System.Reflection.Assembly]::LoadFrom($entLibDll) | Out-Null

        # Find the method -- it may be in different classes depending on version
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

# Step 2e: Fallback -- SQL Browser discovery
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
} # end if ($pmsType)

# Resume transcript — credentials are now in variables, not transcript
try { Start-Transcript -Path $transcriptPath -Append -ErrorAction SilentlyContinue | Out-Null } catch { }

Write-Host ""
Write-Host "  +-------------------------------------+" -ForegroundColor White
Write-Host "  | Server:   $sqlServer" -ForegroundColor White
Write-Host "  | Database: $sqlDatabase" -ForegroundColor White
Write-Host "  | Auth:     $(if ($sqlUser) { "SQL ($sqlUser)" } else { 'Windows' })" -ForegroundColor White
Write-Host "  +-------------------------------------+" -ForegroundColor White

# ============================================
# PHASE 3: Download agent binaries
# ============================================
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
$publicKeyDer = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEBLRvZ572EpqNab9CxJ9/b/GfHpHOrhWkpaaCzIkXQ5d2dwiqdJHlxvrgN0/zCsgp/ccnDXed4DFCkh6wUWCvWA=="
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

# Verify ALL expected binaries have checksum entries before downloading anything
foreach ($bin in $binaries) {
    if (-not $expectedHashes.ContainsKey($bin)) {
        Write-Error "CRITICAL: Checksum missing for $bin -- aborting install"
        exit 1
    }
}

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
        Write-Fail "Download failed for $bin -- $($_.Exception.Message)"
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

# ============================================
# PHASE 4: Write appsettings.json
# ============================================
Write-Step "Phase 4: Writing configuration"

$agentId = "agent-" + ([guid]::NewGuid().ToString("N").Substring(0, 12))
$fingerprint = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Cryptography').MachineGuid

$config = @{
    Agent = @{
        CloudUrl = $CloudUrl
        ApiKey = $ApiKey
        AgentId = $agentId
        PharmacyId = $PharmacyId
        MachineFingerprint = $fingerprint
        Version = "3.0.0"
        SqlServer = $sqlServer
        SqlDatabase = $sqlDatabase
        LearningMode = [bool]$LearningMode
    }
}
if ($sqlUser) {
    $config.Agent.SqlUser = $sqlUser
    $config.Agent.SqlPassword = $sqlPassword
}

$configJson = $config | ConvertTo-Json -Depth 5
$configPath = Join-Path $installDir "appsettings.json"
Set-Content -Path $configPath -Value $configJson -Encoding UTF8

# Write consent receipt to ProgramData — uploaded to cloud on first heartbeat
$consentPath = Join-Path $dataDir "consent-receipt.json"
$consentReceipt.pharmacyId = $PharmacyId
$consentReceipt.agentId = $agentId
$consentJson = $consentReceipt | ConvertTo-Json -Depth 5
Set-Content -Path $consentPath -Value $consentJson -Encoding UTF8
Write-Ok "Consent receipt saved to $consentPath"
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

# ============================================
# PHASE 5: Install Windows services
# ============================================
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

# Install Core (runs as LocalService -- least privilege)
$corePath = Join-Path $installDir "SuavoAgent.Core.exe"
sc.exe create SuavoAgent.Core binPath= "`"$corePath`"" start= delayed-auto obj= "NT AUTHORITY\LocalService" | Out-Null
sc.exe description SuavoAgent.Core "Suavo pharmacy agent - SQL polling, cloud sync" | Out-Null
sc.exe failure SuavoAgent.Core reset= 3600 actions= restart/5000/restart/30000/restart/60000 | Out-Null
sc.exe failureflag SuavoAgent.Core 1 | Out-Null
Write-Ok "SuavoAgent.Core service registered"

# Install Broker (runs as NetworkService -- needs SeTcbPrivilege for WTSQueryUserToken + CreateProcessAsUser)
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

# ============================================
# PHASE 6: Verify
# ============================================
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
Write-Host "  +===========================================+" -ForegroundColor Green
Write-Host "  |   SuavoAgent v3 -- Installation Complete   |" -ForegroundColor Green
Write-Host "  +===========================================+" -ForegroundColor Green
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
    Write-Host "  $($_.Name) -- $sizeMb MB" -ForegroundColor Gray
}

Write-Host ""
Write-Host "  Agent will query for delivery-ready Rxs every 5 minutes." -ForegroundColor Cyan
Write-Host "  Check logs: Get-Content $dataDir\logs\core-*.log -Tail 50" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Install log: $transcriptPath" -ForegroundColor Gray

# Clean up install transcript on success
try {
    Stop-Transcript -ErrorAction SilentlyContinue | Out-Null
    if ($transcriptPath -and (Test-Path $transcriptPath)) {
        Remove-Item $transcriptPath -Force -ErrorAction SilentlyContinue
        Write-Ok "Install transcript cleaned up"
    }
} catch { }
