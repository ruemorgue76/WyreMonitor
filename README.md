# WyreMonitor

A hardware monitor for Windows that draws your PC as a circuit board: CPU chiplets and
threads, memory modules, graphics cards, fans and drives, each in its place. One sentence at
the top tells you whether anything needs attention.

- **Gold** shows how busy a part is.
- **Colour** shows how close a part is to *its own* limit: 45 °C is fine for a CPU and a lot
  for a hard disk.
- Click any part to see all its sensors and a 60-second chart.

It runs in its own window and keeps an icon next to the clock. It needs no browser and
sends nothing over the network.

## Requirements

- Windows 10 (1809 or later) or Windows 11, 64-bit.
- Microsoft Edge WebView2 Runtime: included in Windows 11 and in most Windows 10 installs.
- Administrator rights. Reading CPU temperatures and fans needs a kernel driver. WyreMonitor
  asks once, at install time, and then opens without prompts.
- [PawnIO](https://github.com/namazso/PawnIO), a signed driver that works with Windows
  Memory Integrity (HVCI) turned on. The installer installs it through `winget` if it is missing.

## Install

Download `WyreMonitor-Setup-<version>.exe` from the
[Releases](../../releases) page and run it.

The installer is not code-signed yet, so Windows may show "Windows protected your PC".
Choose **More info**, then **Run anyway**.

The installer:

- copies the app to `C:\Program Files\WyreMonitor`, .NET runtime included;
- creates two scheduled tasks: `WyreMonitor`, which opens the app with administrator rights
  and no UAC prompt, and `WyreMonitor Autostart`, which leaves only the icon at sign-in;
- installs PawnIO if it is missing.

Closing the window keeps the app running next to the clock. **Exit** in the icon's menu quits
it, and **Start with Windows** turns the automatic start on or off. Uninstall it from
Windows Settings › Apps. This removes the app, its tasks and its data, and leaves PawnIO.

## What it shows

| Part | Shown when the hardware reports it |
|---|---|
| CPU | Temperature, load, power and clock. Each thread has its own bar. Ryzen and Threadripper chiplets (CCDs) appear with their own temperature. |
| Graphics cards | Temperature, hot spot, load, video memory, power and fans. A laptop's integrated GPU shows up next to the discrete one. |
| Memory | Usage. The modules appear with their temperature when they have a sensor. |
| Fans and board probes | Read from the motherboard's controller chips. Most laptops don't expose these. |
| Drives | Temperature, used space and SSD remaining life. |
| Battery | Charge level and charging state. |

Parts the PC doesn't report are left out of the drawing.

### Alerts

The sentence at the top lists what needs attention, most serious first:

- temperatures above a warning or critical limit, set per part (see `LIMITS` in `wwwroot/app.js`);
- a fan that was spinning and stopped. Graphics card fans are excluded, since they stop on purpose;
- an SSD below 20 % remaining life;
- the drive Windows runs from below 5 % free space. Other drives never raise space alerts.

Alerts show on screen only: there are no sounds or notifications.

## Build from source

Requirements: the .NET 10 SDK, and Inno Setup 6 for the installer.

```powershell
dotnet build -c Release            # build
.\Start-WyreMonitor.ps1            # build into dist\ and run (closes the installed instance)
.\Build-Installer.ps1              # installer in installer\output\
```

### Releases

The version is written only in `<Version>` in `WyreMonitor.csproj`. The installer reads it
from there, and the app shows it in the icon's menu.

```powershell
winget install --id JRSoftware.InnoSetup -e
winget install --id GitHub.cli -e
gh auth login
.\Publish-Release.ps1 -Version 1.1.0
```

`Publish-Release.ps1` checks that everything is committed and pushed, sets the version,
builds the installer, tags the commit and creates a GitHub Release with the installer
attached. The release notes list the commits since the previous version; `-Notes "..."`
replaces them.

## How it works

| Part | Files | Role |
|---|---|---|
| Sensors | `Hardware/` | Polls LibreHardwareMonitor every second in the background and publishes an immutable snapshot with a 60-sample history per sensor. |
| Local server | `Program.cs` | Serves the page and `/api/sensors` on `127.0.0.1`, port 5005, or the next free one up to 5014. |
| Desktop app | `Desktop/` | WebView2 window, icon next to the clock, single instance, elevation through the scheduled task. |
| Page | `wwwroot/` | The board drawing, the alerts and the detail panel. Plain HTML, CSS and JavaScript, no external requests. |
| Installer | `installer/`, `Build-Installer.ps1` | Inno Setup script and scheduled tasks. |

The server listens only on `127.0.0.1` and exposes read-only sensor data. The single write
endpoint, `/api/show`, brings the window to the front and requires a header that web pages
cannot send.

## License

MIT, see `LICENSE`. Third-party components and their licenses are listed in
`THIRD-PARTY-NOTICES.md`.
