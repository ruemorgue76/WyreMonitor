# WyreMonitor on winget

Lets people install WyreMonitor with `winget install wyremonitor`. The package is a manifest in
Microsoft's public repository [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs):
it points to the installer on this repository's releases and gives its SHA-256. The files under
`manifests/` follow that repository's layout, so they can be submitted as they are.

Package identifier: `ruemorgue76.WyreMonitor`.

## Test on your PC (PowerShell as administrator)

```powershell
winget settings --enable LocalManifestFiles
winget install --manifest .\packaging\winget\manifests\r\ruemorgue76\WyreMonitor\1.5.2
```

The install is silent. Check that WyreMonitor opens from the Start menu and shows readings, then
that `winget uninstall --manifest <same folder>` removes it.

## Submit (once)

```powershell
winget install wingetcreate
wingetcreate submit --token <GitHub token> .\packaging\winget\manifests\r\ruemorgue76\WyreMonitor\1.5.2
```

The token is a GitHub personal access token (classic) with the `public_repo` scope.
wingetcreate forks microsoft/winget-pkgs to your account and opens the pull request. Microsoft's
checks then download, scan and install the package; a moderator merges it, usually within days.

## Later versions

```powershell
wingetcreate update ruemorgue76.WyreMonitor --version 1.5.3 --urls https://github.com/ruemorgue76/WyreMonitor/releases/download/v1.5.3/WyreMonitor-Setup-1.5.3.exe --submit --token <GitHub token>
```
