# Third-party notices

WyreMonitor is released under the GNU General Public License, version 3 or later (see `LICENSE`). It includes or uses the
components below, each under its own license.

## Included in the installer

| Component | License | Source |
|---|---|---|
| LibreHardwareMonitorLib | Mozilla Public License 2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| DiskInfoToolkit | Mozilla Public License 2.0 | https://github.com/Blacktempel/DiskInfoToolkit |
| RAMSPDToolkit-NDD | Mozilla Public License 2.0 | https://github.com/Blacktempel/RAMSPDToolkit |
| BlackSharp.Core | Mozilla Public License 2.0 | https://github.com/Blacktempel/BlackSharp |
| HidSharp, Copyright 2010-2025 James F. Bellinger | Apache License 2.0 | https://software.seekye.com/hidsharp |
| Mono.Posix.NETStandard, © Microsoft Corporation | MIT License | https://github.com/mono/mono |
| Microsoft Edge WebView2 SDK, © Microsoft Corporation | BSD-style license shipped with the package | https://www.nuget.org/packages/Microsoft.Web.WebView2 |
| .NET runtime, ASP.NET Core, Windows Forms, System.Management, © .NET Foundation and contributors | MIT License | https://github.com/dotnet |
| C#/WinRT runtime (WinRT.Runtime), © Microsoft Corporation | MIT License | https://github.com/microsoft/CsWinRT |
| B612 font | SIL Open Font License 1.1 (`wwwroot/fonts/OFL-LICENSE.txt`) | https://github.com/polarsys/b612 |

The MPL-2.0 components are used unmodified. Their source code is available at the
addresses above.

## Installed separately

The installer installs **PawnIO**, the signed driver that reads CPU and motherboard
sensors, if it is missing: it downloads a fixed version from its official releases
(https://github.com/namazso/PawnIO.Setup) and checks its SHA-256. PawnIO is not part of
WyreMonitor and is distributed under its own license.

**Microsoft Edge WebView2 Runtime**, which draws the window, ships with Windows 11 and is
not redistributed by WyreMonitor.
