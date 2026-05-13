#Requires -Version 7.0
<#
.SYNOPSIS
    Tail SuavoAgent.Diagnostics events.jsonl with color-coded signal_kind.

.DESCRIPTION
    Mesh PR 4d delight: 30-line PowerShell pretty-printer for the
    structured event journal at %ProgramData%\SuavoAgent\diagnostics\
    events.jsonl. Run on Queen to watch the mesh in real time without
    leaving the terminal.

.PARAMETER Tail
    Number of recent lines to read. Default 20.

.PARAMETER Follow
    -f equivalent: keep tailing as new events arrive. Ctrl+C to exit.

.EXAMPLE
    pwsh -File tools/tail-events.ps1 -Tail 50 -Follow
#>

[CmdletBinding()]
param(
    [int]$Tail = 20,
    [switch]$Follow,
    [string]$Path = (Join-Path $env:ProgramData "SuavoAgent\diagnostics\events.jsonl")
)

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host "events.jsonl not found at $Path" -ForegroundColor Yellow
    Write-Host "  (Has the agent run since the Mesh PR 4 deploy? Wire writes here on every signal.)" -ForegroundColor DarkGray
    exit 1
}

function Format-EventLine {
    param([string]$Line)
    if ([string]::IsNullOrWhiteSpace($Line)) { return }
    try {
        $evt = $Line | ConvertFrom-Json -ErrorAction Stop
    } catch {
        Write-Host $Line -ForegroundColor DarkGray
        return
    }
    $color = switch ($evt.signal_kind) {
        'managed_exception'    { 'Red' }
        'win32'                { 'Red' }
        'unmanaged_native'     { 'Red' }
        'invariant_violation'  { 'Magenta' }
        'unobserved_task'      { 'Yellow' }
        'exit_code'            { 'Red' }
        'hang'                 { 'Yellow' }
        'heartbeat'            { 'DarkGray' }
        'wire_handler_failed'  { 'DarkRed' }
        default                { 'White' }
    }
    # ConvertFrom-Json in pwsh 7 auto-converts ISO 8601 timestamps to [DateTime].
    # Stringify before .Substring() — DateTime has no Substring method.
    $tsStr = if ($evt.ts -is [datetime]) { $evt.ts.ToString('o') } else { [string]$evt.ts }
    $ts = $tsStr.Substring(0, [math]::Min(19, $tsStr.Length))
    $fpStr = if ($evt.fp_v1) { [string]$evt.fp_v1 } else { '' }
    $fp = if ($fpStr) { $fpStr.Substring(0, [math]::Min(60, $fpStr.Length)) } else { '' }
    Write-Host ("{0,-19} [{1,-8}] {2,-22} {3}" -f $ts, $evt.component, $evt.signal_kind, $fp) -ForegroundColor $color
}

# Initial tail
Get-Content -LiteralPath $Path -Tail $Tail | ForEach-Object { Format-EventLine $_ }

if ($Follow) {
    # Stream new lines as they appear
    Get-Content -LiteralPath $Path -Wait -Tail 0 | ForEach-Object { Format-EventLine $_ }
}
