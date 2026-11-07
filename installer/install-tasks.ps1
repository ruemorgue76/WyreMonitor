# Run by the installer: creates WyreMonitor's two scheduled tasks.
#   WyreMonitor            no trigger: opens the app with administrator rights, without UAC
#   WyreMonitor Autostart  at sign-in: only the icon next to the clock appears
param(
    [Parameter(Mandatory)] [string] $ExePath,
    [switch] $Autostart
)

$ErrorActionPreference = 'Stop'
$user = "$env:USERDOMAIN\$env:USERNAME"

$action = New-ScheduledTaskAction -Execute $ExePath -Argument '--background' `
    -WorkingDirectory (Split-Path -Parent $ExePath)
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
# Priority 4 = normal: the task default (7) would make the window sluggish.
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -Priority 4

Register-ScheduledTask -TaskName 'WyreMonitor' -Action $action -Principal $principal `
    -Settings $settings -Force | Out-Null

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
Register-ScheduledTask -TaskName 'WyreMonitor Autostart' -Action $action -Principal $principal `
    -Settings $settings -Trigger $trigger -Force | Out-Null

if (-not $Autostart) {
    Disable-ScheduledTask -TaskName 'WyreMonitor Autostart' | Out-Null
}
