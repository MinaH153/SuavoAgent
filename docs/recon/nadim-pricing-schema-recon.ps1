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
"@ },

    # ============================================================================================
    # B0 EXTENSION (see B0-pioneerrx-cost-and-reimbursement-sql-recon.md).
    # PRIORITY: Feature A cost path FIRST — "is Cost Per Unit SQL-reachable per NDC?" decides whether
    # pricing's accuracy ceiling is SQL (deterministic) or UIA (DevExpress/virtualization risk).
    # Then Feature B reimbursement path. All READ-ONLY, schema/structure only (no patient/claim rows).
    # ============================================================================================

    # --- Feature A (PRIORITY): cost-per-unit + status reachability ---
    @{ Name = "A_cost_catalog_tables"; Sql = @"
SELECT s.name + '.' + t.name AS qualified_name
FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE t.name LIKE '%Supplier%' OR t.name LIKE '%Vendor%'
   OR t.name LIKE '%ItemCost%' OR t.name LIKE '%Catalog%'
   OR t.name LIKE '%Cost%'     OR t.name LIKE '%Pricing%'
   OR t.name LIKE '%Acquisition%'
ORDER BY 1;
"@ },
    @{ Name = "A_cost_status_columns"; Sql = @"
SELECT s.name AS schema_name, t.name AS table_name,
       c.name AS column_name, tp.name AS data_type, c.is_nullable
FROM sys.columns c
JOIN sys.tables t  ON t.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.types tp  ON tp.user_type_id = c.user_type_id
WHERE c.name LIKE '%CostPerUnit%' OR c.name LIKE '%UnitCost%'
   OR c.name LIKE '%Cost%'        OR c.name LIKE '%Price%'
   OR c.name LIKE '%Discontinued%'OR c.name LIKE '%Available%'
   OR c.name LIKE '%Status%'
ORDER BY t.name, c.column_id;
"@ },
    @{ Name = "A_join_key_columns"; Sql = @"
SELECT s.name AS schema_name, t.name AS table_name, c.name AS column_name, tp.name AS data_type
FROM sys.columns c
JOIN sys.tables t  ON t.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.types tp  ON tp.user_type_id = c.user_type_id
WHERE c.name IN ('NDC','ItemID','ItemNumber','SupplierID','VendorID','SupplierItemNumber')
ORDER BY t.name, c.name;
"@ },
    # NOTE: A_cost_proof (the headline query — supplier rows + cost_per_unit for a real NDC) needs the
    # real table/column names from the three queries above. Run it by hand from the B0 doc TEMPLATE once
    # those are known; it can't be templated blind without risking a bad object reference.

    # --- Feature B (SECOND): reimbursement / preferred-item / drug-group reachability ---
    @{ Name = "B_thirdparty_pricing_tables"; Sql = @"
SELECT s.name + '.' + t.name AS qualified_name
FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE t.name LIKE '%ThirdParty%' OR t.name LIKE '%Plan%'   OR t.name LIKE '%Payer%'
   OR t.name LIKE '%MAC%'        OR t.name LIKE '%Reimburs%'OR t.name LIKE '%Contract%'
   OR t.name LIKE '%Insurance%'  OR t.name LIKE '%Claim%'   OR t.name LIKE '%Adjudicat%'
ORDER BY 1;
"@ },
    @{ Name = "B_reimbursement_preferred_columns"; Sql = @"
SELECT s.name AS schema_name, t.name AS table_name, c.name AS column_name, tp.name AS data_type
FROM sys.columns c
JOIN sys.tables t  ON t.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.types tp  ON tp.user_type_id = c.user_type_id
WHERE c.name LIKE '%Reimburs%' OR c.name LIKE '%Paid%'      OR c.name LIKE '%Allowed%'
   OR c.name LIKE '%MAC%'       OR c.name LIKE '%Preferred%' OR c.name LIKE '%PlanID%'
   OR c.name LIKE '%PayerID%'
ORDER BY t.name, c.column_id;
"@ },
    @{ Name = "B_preferred_item_tables"; Sql = @"
SELECT s.name + '.' + t.name AS qualified_name
FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE t.name LIKE '%Preferred%' OR (t.name LIKE '%Plan%' AND t.name LIKE '%Item%')
ORDER BY 1;
"@ },
    @{ Name = "B_drug_group_columns"; Sql = @"
SELECT s.name AS schema_name, t.name AS table_name, c.name AS column_name, tp.name AS data_type
FROM sys.columns c
JOIN sys.tables t  ON t.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.types tp  ON tp.user_type_id = c.user_type_id
WHERE c.name LIKE '%Equivalent%' OR c.name LIKE '%GenericCode%' OR c.name LIKE '%DrugGroup%'
   OR c.name LIKE '%GPI%'        OR c.name LIKE '%TherapeuticClass%'
ORDER BY t.name, c.column_id;
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
