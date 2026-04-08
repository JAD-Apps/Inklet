#Requires -RunAsAdministrator
# Register-CleanupTask.ps1
# Registers Remove-StaleInkletPackage.ps1 as a one-shot Task Scheduler task
# that runs as SYSTEM at the next startup (before the user logs in and AppXSvc
# acquires file locks on WindowsApps).

$scriptPath = Join-Path $PSScriptRoot "Remove-StaleInkletPackage.ps1"

if (-not (Test-Path $scriptPath)) {
    Write-Error "Cleanup script not found at: $scriptPath"
    exit 1
}

$taskName = "RemoveStaleInkletPackage"

# Remove any pre-existing registration
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue

$action = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument "-NonInteractive -NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""

# AtStartup fires before any user logs in — SYSTEM can still access WindowsApps
# before AppXSvc pins the directory.
$trigger = New-ScheduledTaskTrigger -AtStartup

$principal = New-ScheduledTaskPrincipal `
    -UserId "SYSTEM" `
    -LogonType ServiceAccount `
    -RunLevel Highest

$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 5) `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew

Register-ScheduledTask `
    -TaskName $taskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings `
    -Description "One-shot: removes orphaned JADApps.Inklet_1.0.3.0_x64 staging directory from WindowsApps so the 1.0.3 package can be installed cleanly. Self-deletes after first run." `
    -Force

Write-Host ""
Write-Host "Task '$taskName' registered successfully." -ForegroundColor Green
Write-Host "Please reboot now. The cleanup will run at startup before you log in."
Write-Host "After reboot, run Install.ps1 from the _Test folder to install Inklet 1.0.3."
Write-Host ""
