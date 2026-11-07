<#
    Start-WyreMonitor.ps1
    For development: the installed app opens from the Start menu. This script also closes
    the installed instance. It:
      1. elevates itself (UAC prompt)
      2. closes any running instance
      3. rebuilds the executable into dist\ (when the .NET SDK is installed)
      4. starts WyreMonitor in its window

    Usage: right-click > "Run with PowerShell", or:
      powershell -ExecutionPolicy Bypass -File .\Start-WyreMonitor.ps1
#>

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

# --- 1. Elevate --------------------------------------------------------------
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
Write-Host "=== WyreMonitor ===" -ForegroundColor Cyan

# --- 2. Close running instances ---------------------------------------------
Get-Process WyreMonitor -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

# --- 3. Rebuild into dist\ (when the .NET SDK is available) ------------------
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
if ($dotnet) {
    Write-Host "Building into dist\..." -ForegroundColor DarkGray
    dotnet publish -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o dist | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host "WARNING: build failed (exit code $LASTEXITCODE). Starting the last good executable: it does NOT include recent changes." -ForegroundColor Red
    }
} else {
    Write-Host ".NET SDK not found: using the existing executable." -ForegroundColor DarkGray
}

# --- 4. Start ----------------------------------------------------------------
$exe = Join-Path $here 'dist\WyreMonitor.exe'
if (-not (Test-Path $exe)) { $exe = Join-Path $here 'bin\Release\net10.0-windows\win-x64\WyreMonitor.exe' }
if (-not (Test-Path $exe)) { Write-Error "WyreMonitor.exe not found. Build it with: dotnet build -c Release"; return }

Write-Host "Starting WyreMonitor" -ForegroundColor Green
& $exe
