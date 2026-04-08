#Requires -RunAsAdministrator
# Remove-StaleInkletPackage.ps1
# Deletes the orphaned WindowsApps staging directory left behind by a failed
# Inklet package deployment (0x80073CF9 / 0x80070020 sharing violation).
# Intended to run once at startup (via Task Scheduler) before AppXSvc locks
# the directory again.

$staleDir = "C:\Program Files\WindowsApps\JADApps.Inklet_1.0.3.0_x64__30vn2v44e6ykm"
$logFile  = "$env:TEMP\Remove-StaleInkletPackage.log"

function Write-Log {
    param([string]$Message)
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Message"
    Write-Host $line
    Add-Content -Path $logFile -Value $line
}

Write-Log "=== Remove-StaleInkletPackage started ==="

if (-not (Test-Path $staleDir)) {
    Write-Log "Stale directory not found — nothing to do."
    Write-Log "=== Done ==="
    exit 0
}

Write-Log "Found stale directory: $staleDir"

# Take ownership recursively
Write-Log "Taking ownership..."
$takeown = takeown /f $staleDir /r /d y 2>&1
Write-Log $takeown

# Grant Administrators full control
Write-Log "Granting Administrators full control..."
$icacls = icacls $staleDir /grant "Administrators:F" /t 2>&1
Write-Log $icacls

# Delete the directory
Write-Log "Removing directory..."
try {
    Remove-Item -Path $staleDir -Recurse -Force -ErrorAction Stop
    Write-Log "Successfully removed: $staleDir"
} catch {
    Write-Log "ERROR: Failed to remove directory — $_"
    exit 1
}

# Unregister this scheduled task so it doesn't run again
Write-Log "Unregistering scheduled task..."
try {
    Unregister-ScheduledTask -TaskName "RemoveStaleInkletPackage" -Confirm:$false -ErrorAction SilentlyContinue
    Write-Log "Scheduled task removed."
} catch {
    Write-Log "Warning: could not remove scheduled task — $_"
}

Write-Log "=== Done ==="
exit 0
