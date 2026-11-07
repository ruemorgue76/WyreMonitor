<#
    Publish-Release.ps1 -Version 1.1.0 [-Notes "text"]

    Publishes a new version of WyreMonitor:
      1. checks that everything is committed and in sync with GitHub
      2. writes the version into WyreMonitor.csproj and builds the installer
      3. commits the version, tags it v1.1.0 and pushes both
      4. creates the GitHub Release with the installer attached

    Without -Notes, the release notes list the commits since the previous version.
    Needs GitHub CLI signed in: winget install --id GitHub.cli -e, then gh auth login
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Notes
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# git also writes normal messages to stderr: with 'Stop', Windows PowerShell 5.1 would treat
# them as errors. Only the exit code matters here.
function Invoke-Git {
    $ErrorActionPreference = 'Continue'
    $out = & git.exe @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed" }
    $out
}

# --- 1. Checks, before touching anything -----------------------------------------
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid version: use the 1.2.3 format" }
$tag = "v$Version"

$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = "$env:ProgramFiles\GitHub CLI\gh.exe" }
if (-not (Test-Path $gh)) { throw "GitHub CLI not found. Install it with: winget install --id GitHub.cli -e" }
& $gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw "GitHub CLI is not signed in. Run: gh auth login" }

if (Invoke-Git status --porcelain) { throw "There are uncommitted changes: commit them first." }
if ((Invoke-Git branch --show-current) -ne 'main') { throw "Releases are published from the main branch." }
Invoke-Git fetch origin --tags --quiet
if ((Invoke-Git rev-parse HEAD) -ne (Invoke-Git rev-parse origin/main)) { throw "The local branch is not in sync with GitHub: pull or push first." }
if (Invoke-Git tag --list $tag) { throw "Version $Version already exists." }

$csproj = 'WyreMonitor.csproj'
$current = ([xml](Get-Content $csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ([version]$Version -lt [version]$current) { throw "The version must be at least $current." }

# Release notes: the commits since the previous version (all of them for the first one).
if (-not $Notes) {
    $previous = try { Invoke-Git describe --tags --abbrev=0 2>$null } catch { $null }   # no tag yet: first release
    $range = if ($previous) { "$previous..HEAD" } else { 'HEAD' }
    $lines = Invoke-Git log $range --no-merges --format='- %s' | Where-Object { $_ -notmatch '^- Version \d' }
    $Notes = "## What's new`n`n" + ($lines -join "`n")
}

# --- 2. Version and installer ------------------------------------------------------
$bumped = $false
if ($Version -ne $current) {
    $text = Get-Content $csproj -Raw
    $text = $text -replace "<Version>$([regex]::Escape($current))</Version>", "<Version>$Version</Version>"
    [IO.File]::WriteAllText((Resolve-Path $csproj), $text)
    $bumped = $true
}

try {
    $setup = & .\Build-Installer.ps1 | Select-Object -Last 1
}
catch {
    if ($bumped) { Invoke-Git checkout -- $csproj | Out-Null }
    throw
}

# The installer is not code-signed: publish its SHA-256 so downloads can be checked.
$hash = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$Notes += "`n`n## Verify the download`n`nSHA-256 of ``$($setup.Name)``:`n`n``````text`n$hash`n```````n`nIn PowerShell: ``Get-FileHash .\$($setup.Name)``"

# --- 3. Commit, tag, push ----------------------------------------------------------
if ($bumped) {
    Invoke-Git add $csproj | Out-Null
    Invoke-Git commit -m "Version $Version" | Out-Null
}
Invoke-Git tag -a $tag -m "WyreMonitor $Version" | Out-Null
Invoke-Git push --quiet origin main $tag | Out-Null

# --- 4. Release with the installer -------------------------------------------------
$notesFile = [IO.Path]::GetTempFileName()
[IO.File]::WriteAllText($notesFile, $Notes)
try {
    & $gh release create $tag $setup.FullName --title "WyreMonitor $Version" --notes-file $notesFile
    if ($LASTEXITCODE -ne 0) {
        throw "Release not created. The version and tag are already on GitHub: retry with`n  gh release create $tag `"$($setup.FullName)`" --title `"WyreMonitor $Version`""
    }
}
finally {
    Remove-Item $notesFile -ErrorAction SilentlyContinue
}

Write-Host "Published WyreMonitor $Version" -ForegroundColor Green
