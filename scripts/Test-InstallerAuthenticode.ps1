[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Path,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Fa-f0-9]{64}(,[A-Fa-f0-9]{64}){0,15}$')]
    [string]$AllowedSignerSha256
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path -LiteralPath $kits -PathType Container)) { return $null }
    return Get-ChildItem -LiteralPath $kits -Directory |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
}

$item = Get-Item -LiteralPath $Path -Force
if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Installer signature input must be a regular non-reparse-point file.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $item.FullName
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $null -eq $signature.SignerCertificate) {
    throw "Installer Authenticode signature is invalid: $($signature.Status)."
}
$allowed = $AllowedSignerSha256.Split(',') | ForEach-Object { $_.ToUpperInvariant() }
$actual = $signature.SignerCertificate.GetCertHashString(
    [Security.Cryptography.HashAlgorithmName]::SHA256).ToUpperInvariant()
if ($actual -notin $allowed) { throw 'Installer signer is outside the approved publisher set.' }

if ($null -eq $signature.TimeStamperCertificate) {
    throw 'Installer is missing its RFC3161 timestamp certificate.'
}
$timestampEku = $false
foreach ($extension in $signature.TimeStamperCertificate.Extensions) {
    if ($extension -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
        foreach ($oid in $extension.EnhancedKeyUsages) {
            if ($oid.Value -eq '1.3.6.1.5.5.7.3.8') { $timestampEku = $true }
        }
    }
}
if (-not $timestampEku) { throw 'Installer timestamp certificate lacks the timestamping EKU.' }

$signTool = Find-SignTool
if (-not $signTool) { throw 'signtool.exe is required for RFC3161 verification.' }
& $signTool verify /pa /all /tw $item.FullName
if ($LASTEXITCODE -ne 0) { throw 'signtool /tw rejected the installer timestamp.' }

Write-Output "Verified timestamped installer: $($item.Name)"
