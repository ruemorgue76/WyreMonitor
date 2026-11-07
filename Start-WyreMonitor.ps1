<#
    Start-WyreMonitor.ps1
    For development: runs the code in this folder instead of the installed app.
      1. elevates itself (UAC prompt): the service half needs administrator rights
      2. stops the installed service and app
      3. builds into dist\
      4. starts dist\WyreMonitor.exe --service in a console window, then the app

    When done, close both and restart the installed service:  sc.exe start WyreMonitor

    Usage: right-click > "Run with PowerShell", or:
      powershell -ExecutionPolicy Bypass -File .\Start-WyreMonitor.ps1
#>

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Asking for administrator rights (accept the UAC prompt)..." -ForegroundColor Yellow
    Start-Process -FilePath 'powershell.exe' `
        -ArgumentList "-NoExit -ExecutionPolicy Bypass -File `"$PSCommandPath`"" `
        -Verb RunAs
    return
}

Set-Location $here
Write-Host "=== WyreMonitor (development) ===" -ForegroundColor Cyan

& "$env:SystemRoot\System32\sc.exe" stop WyreMonitor | Out-Null
Get-Process WyreMonitor -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Host "Building into dist\..." -ForegroundColor DarkGray
dotnet publish -c Release -r win-x64 --self-contained -o dist | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Host "Build failed (exit code $LASTEXITCODE)." -ForegroundColor Red; return }

$exe = Join-Path $here 'dist\WyreMonitor.exe'
Start-Process $exe -ArgumentList '--service' -WindowStyle Minimized
Start-Sleep -Seconds 2
Start-Process $exe
Write-Host "Running. Afterwards: sc.exe start WyreMonitor" -ForegroundColor Green
