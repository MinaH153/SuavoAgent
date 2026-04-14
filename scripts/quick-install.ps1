# DEPRECATED — This script has been removed for security reasons.
#
# Issues:
#   - No ECDSA signature verification on downloaded binaries (supply chain risk)
#   - Destroyed audit chain on reinstall (HIPAA violation)
#   - Hardcoded stale version and TEST-001 pharmacy ID
#
# Use bootstrap.ps1 instead:
#   irm https://raw.githubusercontent.com/MinaH153/SuavoAgent/main/bootstrap.ps1 | iex
#
# Or use SuavoSetup.exe for the full guided installer.

Write-Host ""
Write-Host "  quick-install.ps1 has been DEPRECATED for security reasons." -ForegroundColor Red
Write-Host ""
Write-Host "  Use bootstrap.ps1 instead:" -ForegroundColor Yellow
Write-Host "    irm https://raw.githubusercontent.com/MinaH153/SuavoAgent/main/bootstrap.ps1 | iex" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Or download SuavoSetup.exe from the latest GitHub release." -ForegroundColor Yellow
Write-Host ""
exit 1
