<h1 align="center">
  <img src="docs/images/logo.png" width="40" alt="" /><br />
  WyreMonitor
</h1>

<p align="center">
  A hardware monitor for Windows that draws your PC as a circuit board<br />
  and tells you in one sentence whether anything needs attention.
</p>

<p align="center">
  <a href="../../releases/latest"><img src="https://img.shields.io/github/v/release/ruemorgue76/WyreMonitor?label=Download&style=flat" alt="Download the latest release" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/ruemorgue76/WyreMonitor?style=flat" alt="GPL-3.0 license" /></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078d4?style=flat" alt="Windows 10 and 11" />
  <a href="../../releases"><img src="https://img.shields.io/github/downloads/ruemorgue76/WyreMonitor/total?style=flat" alt="Downloads" /></a>
</p>

![WyreMonitor showing a Threadripper 3960X, an RTX 4090, eight fans and eight drives](docs/images/overview.png)

## Why another hardware monitor?

Most monitors show a long list of sensors and leave the reading to you. WyreMonitor puts each
part where it sits on the board and answers the question you opened it for:

- **A verdict on top.** "All clear", or a short list of what needs checking, most serious
  first. Click an item to jump to that part.
- **Limits per part.** Colour shows how close each reading is to *that part's* limit:
  45 °C is fine for a CPU and a lot for a hard disk.
- **The real layout.** Ryzen and Threadripper chiplets each get their own temperature, and every
  thread has its own load bar. Memory modules sit beside the socket, drives below. The board
  takes the room its parts need: a small office PC gets a small board, a workstation a big one.
- **Every sensor, when you want it.** Click any part to see all its readings and a chart of the
  last 1, 5 or 15 minutes.
- **Read at a glance.** Gold pads show how busy a part is, grey bars how full, and the colour of
  a reading how close it is to its limit; the part nearest its limit is outlined, and the
  board's debug display shows the code of what needs attention.

<p align="center">
  <img src="docs/images/alerts.png" width="100%" alt="The verdict saying the CPU is above its critical limit, with a link to the CPU and one to change the limit" /><br />
  <sub>How alerts look. The CPU limit in this picture was lowered by hand to show one.</sub>
</p>

## Features

- CPU temperature, load, power and clock, with per-chiplet temperature and per-thread load
- Graphics cards: temperature, hot spot, load, video memory, power and fans. A laptop's integrated
  and discrete GPUs appear side by side
- Motherboard fans and temperature probes, memory modules with their temperature
- Drives: temperature, used space and SSD remaining life
- Battery charge and charging state on laptops
- Alerts for high temperatures, stopped fans, worn SSDs and a nearly full Windows drive
- Settings: °C or °F, values updated once or twice a second, charts of the last 1, 5 or 15
  minutes, fan icons spinning at their fan's pace or still, and a detail panel you can hide.
  The window reopens where you left it
- Four board colours (green, matte black, blue, white), colours for colour blindness, high
  contrast, three sizes and a compact layout; traces, mounting holes and animations can be turned
  off. The window, the small window and the icon follow the choice
- Your own names for fans and probes ("Front intake", "VRM"), and parts you don't care about
  hidden from the board
- Alert limits you can change, and each kind of alert can be turned off
- Optional: a reading of your choice on the icon next to the clock (CPU, graphics card or memory),
  and Windows notifications when something needs attention, also with the window closed
- A list view with every sensor, its current, lowest and highest value
- A small window that stays on top of the others, with CPU, graphics card and memory. The main
  window, the small one and the icon take turns: one place shows the readings at a time
- Export of the last 15 minutes of every sensor as a CSV file, for a spreadsheet
- Update check, from the icon's menu or once a day if you turn it on
- Own window and an icon next to the clock. It starts with Windows and opens without UAC prompts
- Nothing leaves your PC: the page is served only on `127.0.0.1`. The only request to the
  internet is the update check, and only when you ask for it or turn it on

Parts a PC doesn't report are left out of the drawing instead of showing up empty.

<p align="center">
  <img src="docs/images/detail.png" width="420" alt="Detail panel of a Threadripper 3960X with a one-minute temperature chart" />
</p>

## Install

1. Download `WyreMonitor-Setup-<version>.exe` from the [latest release](../../releases/latest).
2. Check the download if you like: the release notes list its SHA-256, and
   `Get-FileHash .\WyreMonitor-Setup-<version>.exe` in PowerShell prints the one you have.
3. Run it. The installer is not code-signed yet, so Windows may show "Windows protected your PC":
   choose **More info**, then **Run anyway**.
4. Open WyreMonitor from the Start menu.

WyreMonitor has two parts:

- **The WyreMonitor service** reads the sensors. It runs in the background as a Windows service,
  starts with Windows, and serves the data on `127.0.0.1` only. When no window has shown the data
  for a minute, it stops reading the sensors until one does.
- **The app** shows the window and the icon next to the clock. It runs with your normal rights,
  never as administrator.

The installer puts both in `C:\Program Files\WyreMonitor`, with the .NET runtime included. If
[PawnIO](https://github.com/namazso/PawnIO.Setup) is missing, it downloads a fixed version from
its official releases, checks the file's SHA-256, then installs it.

Closing the window keeps the app next to the clock. **Exit** in the icon's menu quits it, and
**Start with Windows** turns the icon at sign-in on or off.

**Uninstall** from Windows Settings › Apps. This removes the app, the service and their settings.
PawnIO stays, since other programs may use it. The window's browser data stays in
`%LOCALAPPDATA%\WyreMonitor`, and you can delete that folder yourself.

### Requirements

- Windows 10 (1809 or later) or Windows 11, 64-bit
- Microsoft Edge WebView2 Runtime, included in Windows 11 and in most Windows 10 installs
- An administrator account, to install it

## FAQ

**Why does it need administrator rights?**
Reading CPU temperatures, fans and motherboard probes goes through a kernel driver, which only
administrators can use. You confirm once, when installing. From then on only the WyreMonitor
service uses the driver, as a standard Windows service. The app you see runs with your normal
rights, so opening it never asks for confirmation.

**Is the service safe to leave running?**
It only reads sensors and listens on `127.0.0.1`, so other computers cannot reach it. It refuses
requests addressed to any other host name, which blocks web pages that try to reach it through
their own domain. It takes no settings from your user account, and writes only its port number,
to a registry key that only administrators can change. With no window open for a minute it stops
reading the sensors and uses almost no CPU.

**Does it work on laptops?**
Yes for CPU, graphics, memory, drives and battery. Most laptops don't expose their fans and board
probes, so those parts simply don't appear.

**Can it control fan speeds?**
No, WyreMonitor only reads sensors. For fan curves, look at
[Fan Control](https://github.com/Rem0o/FanControl.Releases).

**My antivirus flags it. Is it safe?**
The source code is all here and you can build it yourself. WyreMonitor uses PawnIO, a signed
driver that works with Windows Memory Integrity turned on, instead of the old WinRing0 driver
that many antivirus programs block. Some antivirus programs still distrust new apps that install
a service and use a driver.

**Does it send any data?**
No. It talks only to your own hardware and serves its page on `127.0.0.1`, which other
computers cannot reach. The update check asks GitHub for the latest release, sending nothing
but the app's name and version; it runs only when you choose *Check for updates* in the icon's
menu, or once a day if you turn on *Check for updates automatically* (off by default). A new
version is offered as a link to its download page: WyreMonitor never downloads or runs anything
by itself.

**A sensor is missing or reads wrong. What can I do?**
WyreMonitor reads sensors through [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor),
so hardware support depends on it. Problems with missing or wrong readings belong there. Problems
with how WyreMonitor shows them belong in this repository's [issues](../../issues).

**Can I show temperatures in Fahrenheit?**
Yes: open **Settings** at the top right and choose Fahrenheit.

**How do I get notifications, or a reading next to the clock?**
Right-click the WyreMonitor icon next to the clock. Under *Show on this icon* pick the reading
(CPU, graphics card or memory: temperature, load or power); a coloured strip tells which part it
is, gold for the CPU, blue for the graphics card, violet for memory. The icon shows it while both
windows are closed. *Notify me when something needs attention* works whatever is open. Both use
the limits and names you set in the window.

**Main window, small window or icon?**
One at a time. *Small window* in the main window's header swaps it for the small one; a double
click on the small window brings the main one back. Close both and the icon takes over.

**How much CPU does it use?**
Measured on a Threadripper 3960X, in % of one core: main window open about 13 % (less with still
fan icons, about 17 % when updating twice a second); only the small window about 4 %; only a
reading on the icon, updated every second, about 2 %; nothing open and nothing on the icon,
almost nothing. Most of it is reading the CPU and graphics card sensors, not drawing them.

## Build from source

You need the .NET 10 SDK, and Inno Setup 6 for the installer.

```powershell
dotnet build -c Release            # build
.\Start-WyreMonitor.ps1            # build into dist\ and run service + app (stops the installed ones)
.\Build-Installer.ps1              # installer in installer\output\
```

<details>
<summary>Publishing a release</summary>

The version is written only in `<Version>` in `WyreMonitor.csproj`. The installer reads it from
there, and the app shows it in the icon's menu.

```powershell
winget install --id GitHub.cli -e
gh auth login
.\Publish-Release.ps1 -Version 1.1.0
```

`Publish-Release.ps1` checks that everything is committed and pushed, sets the version, and
pushes a tag carrying the release notes. GitHub Actions then builds the installer and creates the
release with it attached (see *Code signing policy*). Once signing is set up
([docs/signpath](docs/signpath)), the build waits for the signing requests to be approved on
SignPath. The release notes list the commits since
the previous version; `-Notes "..."` replaces them.

Without a PC set up for this, a release can also be published from the browser: set `<Version>` in
`WyreMonitor.csproj` on `main`, then **Actions > Release > Run workflow** on `main` with **Publish**
ticked. The notes come from `release-notes/<version>.md` when that file exists, otherwise from the
commits since the previous version.

</details>

<details>
<summary>How it works</summary>

| Part | Files | Role |
|---|---|---|
| Entry point | `Program.cs` | One executable: `--service` runs the service, no argument runs the app. |
| Sensors | `Hardware/` | Reads LibreHardwareMonitor in the background, each part at its own pace, and publishes an immutable snapshot with a 60-second history per sensor. |
| Service | `Service/` | Windows service (LocalSystem). Serves the page and a read-only `/api/sensors` on `127.0.0.1`, port 5005 or the next free one up to 5014, and writes the port to `HKLM\SOFTWARE\WyreMonitor`. |
| App | `Desktop/` | Runs with the user's rights: WebView2 window locked to the service's page, icon next to the clock, single instance, start at sign-in through the user's Run key. |
| Page | `wwwroot/` | The board drawing, the alerts and the detail panel. Plain HTML, CSS and JavaScript, no external requests. |
| Installer | `installer/`, `Build-Installer.ps1` | Inno Setup script: files, service, PawnIO (pinned version, SHA-256 checked). |

Security choices:

- **Rights:** the service is the only part with administrator rights. It accepts no input that a
  normal user could change: no command-line or environment configuration, no files in user
  folders, and .NET startup hooks are turned off.
- **Server:** listens only on `127.0.0.1`, accepts only the `127.0.0.1` and `localhost` host names
  (which blocks DNS rebinding), and sends a strict Content-Security-Policy.
- **Window:** the app's window can't navigate elsewhere, open new windows or use developer tools.

</details>

## Code signing policy

The installer is not code-signed yet. To check that a download is genuine, compare its SHA-256
with the one in the release notes (see *Install*).

Every installer on the [releases page](../../releases) is built by GitHub Actions from the code in
this repository, with the steps in [`.github/workflows/release.yml`](.github/workflows/release.yml):
the commit, the build script and the build log are public. Nothing published is built on a
personal PC. Each release lists the installer's SHA-256.

The release workflow is ready to sign through [SignPath](https://signpath.io) once the project
has a code signing certificate ([setup](docs/signpath)). It would sign only files built from this
repository: `WyreMonitor.exe`, `WyreMonitor.dll` and the installer. The .NET runtime, the
libraries of other projects and the PawnIO driver keep their authors' own signatures.

Team roles. WyreMonitor has one maintainer, Claudio Cianchi, who holds every role and signs in
to GitHub with multi-factor authentication:

- Committers and reviewers: [ruemorgue76](https://github.com/ruemorgue76). Changes proposed by
  anyone else (pull requests) are reviewed before they are merged.
- Approvers (who approve each release): [ruemorgue76](https://github.com/ruemorgue76)

Privacy: this program will not transfer any information to other networked systems unless
specifically requested by the user or the person installing or operating it. In practice that
means two requests, both to GitHub: the installer downloads the PawnIO driver from its official
releases, and the app asks for the latest WyreMonitor version when you choose *Check for
updates* or turn on the daily check. See [GitHub's privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).

## Built with

| Library | Used for |
|---|---|
| [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | Reading every sensor |
| [PawnIO](https://github.com/namazso/PawnIO.Setup) | Signed driver for CPU and motherboard sensors |
| [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) | The window |
| [B612](https://github.com/polarsys/b612) | Typeface, designed by Airbus for cockpit displays |
| [Inno Setup](https://jrsoftware.org/isinfo.php) | The installer |

## License

Copyright © 2026 Claudio Cianchi ([ruemorgue76](https://github.com/ruemorgue76)).

WyreMonitor is written and maintained by Claudio Cianchi.

WyreMonitor is free software: you can use, study, share and change it under the terms of the
[GNU General Public License](LICENSE), version 3 or any later version. If you distribute a
modified version, it must be under the same license, with its source code. Versions up to 1.3.0
were released under the MIT License. Third-party components and their licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
