<#
    Build-Installer.ps1
    Builds installer\output\WyreMonitor-Setup-<version>.exe:
      1. reads the version from WyreMonitor.csproj (the only place it is written)
      2. publishes the app, .NET runtime included, to publish\
      3. packages it with Inno Setup 6 (winget install --id JRSoftware.InnoSetup -e)
    Returns the installer file.
#>

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = ([xml](Get-Content WyreMonitor.csproj -Raw)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in WyreMonitor.csproj" }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup not found. Install it with: winget install --id JRSoftware.InnoSetup -e" }

if (Test-Path publish) { Remove-Item publish -Recurse -Force }

Write-Host "WyreMonitor $version" -ForegroundColor Cyan
dotnet publish -c Release -r win-x64 --self-contained -o publish | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit code $LASTEXITCODE)" }

& $iscc /Q "/DAppVersion=$version" installer\WyreMonitor.iss | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Installer build failed (exit code $LASTEXITCODE)" }

Get-Item "installer\output\WyreMonitor-Setup-$version.exe"
