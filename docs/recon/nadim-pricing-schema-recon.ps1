<#
.SYNOPSIS
    Pre-Saturday (2026-04-25) schema reconnaissance against Nadim's PioneerRx SQL server.

.DESCRIPTION
    Joshua runs this over Chrome Remote Desktop while signed into Nadim's pharmacy PC (PIONEER10).
    Output is written to C:\SuavoAgent\recon\pioneer-pricing-recon-{timestamp}.json — attach to
    a follow-up session so we can finalize the query before Saturday morning.

    The script is READ-ONLY. Every statement is a SELECT. No UPDATE, INSERT, DELETE, or DDL.

.NOTES
    Requires: sqlcmd.exe (Microsoft.SqlServer.SqlClient or MSSQL tools). Pre-installed on any box
    that runs PioneerRx, so no download needed.

    If the connection string can't be obtained via SuavoAgent's Enterprise Library discovery path,
    fall back to SQL Server Management Studio and paste the queries under "SQL queries" below.

.PARAMETER Server
    PioneerRx SQL server host (e.g., PIONEERSERVER, 192.168.0.10, PIONEERSERVER\NewTech).

.PARAMETER Database
    Default: PioneerPharmacySystem.

.PARAMETER SqlUser
    SQL Auth user. Default: PioneerPharmacyUser.

.PARAMETER SqlPassword
    SQL Auth password — leave empty to prompt.
#>

param(
    [string]$Server       = $(Read-Host "SQL server (e.g., PIONEERSERVER,49202\NewTech)"),
    [string]$Database     = "PioneerPharmacySystem",
    [string]$SqlUser      = "PioneerPharmacyUser",
    [SecureString]$SqlPassword = $(Read-Host -AsSecureString "SQL password")
)

$ErrorActionPreference = "Stop"
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir    = "C:\SuavoAgent\recon"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$outFile   = Join-Path $outDir "pioneer-pricing-recon-$timestamp.json"
$rawFile   = Join-Path $outDir "pioneer-pricing-recon-$timestamp.txt"

$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SqlPassword)
$plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) | Out-Null

Write-Host "Running recon queries against $Server / $Database ..." -ForegroundColor Cyan

# --------------------------------------------------------------------------------------------------
# SQL queries
# --------------------------------------------------------------------------------------------------

$queries = @(
    @{ Name = "inventory_schema_tables";  Sql = @"
SELECT s.name AS schema_name, t.name AS table_name
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name IN ('Inventory','Purchasing','Ordering')
ORDER BY 1, 2;
"@ },
    @{ Name = "inventory_schema_columns"; Sql = @"
SELECT s.name AS schema_name, t.name AS table_name,
       c.name AS column_name, tp.name AS data_type, c.is_nullable
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.columns c ON c.object_id = t.object_id
JOIN sys.types tp ON tp.user_type_id = c.user_type_id
WHERE s.name IN ('Inventory','Purchasing','Ordering')
ORDER BY 1, 2, c.column_id;
"@ },
    @{ Name = "ndc_samples";              Sql = @"
SELECT TOP 20 ItemID, ItemName, NDC
FROM Inventory.Item
WHERE NDC IS NOT NULL AND LEN(NDC) > 0
ORDER BY NEWID();
"@ },
    @{ Name = "supplier_or_vendor_tables"; Sql = @"
SELECT s.name + '.' + t.name AS qualified_name, OBJECT_ID(s.name + '.' + t.name) AS object_id
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE t.name LIKE '%Supplier%' OR t.name LIKE '%Vendor%'
ORDER BY 1;
"@ }
)

$aggregate = @{}
foreach ($q in $queries) {
    Write-Host ("  [{0}]" -f $q.Name) -ForegroundColor DarkCyan
    $tmp = Join-Path $outDir ("q_{0}_{1}.tsv" -f $q.Name, $timestamp)
    & sqlcmd -S $Server -d $Database -U $SqlUser -P $plain -Q $q.Sql -s "`t" -W -h -1 -o $tmp 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "sqlcmd exit code $LASTEXITCODE for $($q.Name) — continuing"
        $aggregate[$q.Name] = @{ error = "exit_$LASTEXITCODE"; raw = (Get-Content $tmp -Raw) }
        continue
    }
    $lines = Get-Content $tmp
    $aggregate[$q.Name] = @{
        row_count = ($lines | Where-Object { $_ -and $_ -notmatch "^-+`t-+" }).Count - 1  # subtract header
        rows      = $lines
    }
}

$aggregate | ConvertTo-Json -Depth 6 | Set-Content -Path $outFile -Encoding UTF8
Get-ChildItem -Path $outDir -Filter "q_*_$timestamp.tsv" | Get-Content | Set-Content -Path $rawFile -Encoding UTF8
Remove-Item -Path (Join-Path $outDir "q_*_$timestamp.tsv") -Force

# Zero the plaintext password from memory.
$plain = $null

Write-Host ""
Write-Host "Recon saved to:" -ForegroundColor Green
Write-Host "  $outFile" -ForegroundColor Green
Write-Host "  $rawFile" -ForegroundColor Green
Write-Host ""
Write-Host "Next step: send both files to Joshua. Keeps locally, no cloud upload." -ForegroundColor Yellow
