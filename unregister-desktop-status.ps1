# unregister-desktop-status.ps1 - removes only "\DEG\DEG Desktop Status". Touches no other task.
if (Get-ScheduledTask -TaskPath '\DEG\' -TaskName 'DEG Desktop Status' -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskPath '\DEG\' -TaskName 'DEG Desktop Status' -Confirm:$false
    Write-Host 'Removed \DEG\DEG Desktop Status'
} else { Write-Host 'Task was not registered.' }
