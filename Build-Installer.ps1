<#
    Build-Installer.ps1 [-PublishOnly | -PackageOnly]
    Builds installer\output\WyreMonitor-Setup-<version>.exe:
      1. reads the version from WyreMonitor.csproj (the only place it is written)
      2. publishes the app, .NET runtime included, to publish\
      3. packages it with Inno Setup 6 (winget install --id JRSoftware.InnoSetup -e)
    Returns the installer file.

    The two steps can run separately, so that the release workflow can sign WyreMonitor.exe and
    WyreMonitor.dll in publish\ before they are packaged:
      -PublishOnly   step 2 only; returns the publish folder
      -PackageOnly   step 3 only, from the publish\ folder already there
#>
param(
    [switch] $PublishOnly,
    [switch] $PackageOnly
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if ($PublishOnly -and $PackageOnly) { throw "Use -PublishOnly or -PackageOnly, not both." }

$version = ([xml](Get-Content WyreMonitor.csproj -Raw)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in WyreMonitor.csproj" }
Write-Host "WyreMonitor $version" -ForegroundColor Cyan

if (-not $PackageOnly) {
    if (Test-Path publish) { Remove-Item publish -Recurse -Force }
    dotnet publish -c Release -r win-x64 --self-contained -o publish | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit code $LASTEXITCODE)" }
    if ($PublishOnly) { return Get-Item publish }
}

if (-not (Test-Path publish\WyreMonitor.exe)) { throw "publish\WyreMonitor.exe not found: run without -PackageOnly first." }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup not found. Install it with: winget install --id JRSoftware.InnoSetup -e" }

& $iscc /Q "/DAppVersion=$version" installer\WyreMonitor.iss | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Installer build failed (exit code $LASTEXITCODE)" }

Get-Item "installer\output\WyreMonitor-Setup-$version.exe"
