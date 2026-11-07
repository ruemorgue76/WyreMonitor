# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

The interface is a web page shown inside a Windows desktop app (WebView2), served by a local Windows service. It is designed and rendered as web UI; the app around it draws three native surfaces of its own: the window's title bar, a small always-on-top window and the number on the tray icon.

## Users

Three audiences, all primary, served in layers:

- **PC enthusiasts**: built or care about their machine; check temperatures, fans and power out of curiosity and for peace of mind. Know the terms (CCD, hot spot, NVMe) but do not want a wall of numbers.
- **Everyday users**: want to know whether the PC is fine. Need a clear verdict and a warning when something needs attention, without hardware knowledge.
- **Overclockers and technicians**: tune and push the machine; want every sensor, exact values, lowest and highest, detailed charts.

## Product Purpose

WyreMonitor reads the hardware sensors of a Windows PC and shows them as a plan of that machine, with one sentence on top saying whether everything is fine or what to check. Success: anyone glancing at it knows in seconds whether the PC is healthy, and anyone curious can go as deep as every single sensor.

## Positioning

Four things together, which HWiNFO and LibreHardwareMonitor's own window do not offer:

- **The plan of the machine**: the user's actual hardware drawn as it is (CPU chiplets and threads, memory modules, graphics cards, fans, drives), not a list of sensors.
- **The verdict**: a plain sentence on top, using per-part limits (a temperature that is fine for a CPU is a lot for a hard disk).
- **Light and private**: little CPU, no network except an optional update check, no account, open source (GPL-3.0).
- **Good to keep open**: a cared-for object people are happy to leave on screen and to show in screenshots.

## Operating Context

- **On a second monitor**, open for a long time while gaming, working or rendering, read out of the corner of the eye: values must be legible at a distance and the state recognisable without reading.
- **A quick look** when something seems off (noisy fans, slow PC): understand what is happening in a few seconds.
- **Mostly closed**: the small always-on-top window or the reading on the tray icon, plus Windows notifications.
- Window sizes from about 1000 × 640 up to full screen on large monitors; the page also adapts down to phone width.
- Readings refresh every second (twice a second optionally); charts cover the last 1, 5 or 15 minutes.

## Capabilities and Constraints

- Windows only. Sensors through LibreHardwareMonitor with the PawnIO driver; what a PC reports varies a lot (desktops with many fans and probes, laptops with almost none). Parts a PC does not report are left out, never shown empty.
- Read-only: WyreMonitor never controls fans or clocks.
- The window is locked to the page: no navigation, no downloads; strict Content Security Policy (scripts, styles, fonts and images only from the page's own server). Fonts must be self-hosted.
- CPU use matters: animations that make the whole window repaint every frame are not acceptable (measured: a CSS fan animation once cost about 30 % of a core).
- Existing functions the redesign must keep: verdict and alerts; plan of the machine; detail panel with chart and every sensor; list view; settings (units, refresh rate, chart length, fan icons, appearance options, alert limits, hidden parts, own names); small window; tray reading; notifications; CSV export; update check.
- Interface language: English.
- Terminology in use: board, part, sensor, probe, verdict, alert, detail panel, list view, small window.

## Brand Commitments

- Name: WyreMonitor.
- Voice: plain, short sentences; names things as a person would ("Fan A1 stopped", "Windows drive almost full"); no hype.

## Evidence on Hand

- Real readings from a Threadripper 3960X with an RTX 4090, 8 fans, 8 drives and 4 memory modules (the developer's PC), plus simulated machines for testing (laptop, Ryzen desktop): `testing/fixtures.js`.
- Screenshots of the current version: `docs/images/`.
- No testimonials, user counts, benchmarks or press: none may be invented.

## Product Principles

1. **State before numbers.** Whether something needs attention is visible before any value is read.
2. **Depth on demand.** The verdict for everyone, the plan for the curious, every sensor for the expert; each layer one action away, none forced on the others.
3. **The user's own machine.** Show the hardware this PC really has, as it is laid out; never a generic template.
4. **Calm by default.** Something to live with on a second monitor: no motion or colour that does not carry information.
5. **Light and honest.** Little CPU, nothing leaves the PC, nothing claimed that the sensors do not say.

## Accessibility & Inclusion

- Text contrast at least 4.5:1, controls and meaningful graphics at least 3:1, in every theme.
- Colour is never the only signal (state also in words, position or shape); a colour-blind-friendly palette and a high-contrast option exist and must remain.
- Respects Windows "reduce animations"; three text sizes.
