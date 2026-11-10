# Fans: what WyreMonitor shows and when it warns

Status: rules agreed with the maintainer on 10 November 2026 (second version, after 1.5.4).

## What the hardware tells us

A fan header has a tachometer wire: the fan pulls it to ground twice per turn, and the
motherboard's Super I/O chip turns the time between pulses into RPM. That is the only reading.

- No pulses means 0 RPM. A fan standing still, an empty header, a broken fan and an unplugged
  cable all read exactly the same. No common chip can tell whether something is connected.
- A signal (pulses, so RPM above 0) is proof that a fan is connected: nothing else makes them.
- Many fans stop on purpose: almost every graphics card, many chipset fans, and any fan the BIOS
  "Fan Stop" setting turns off while things are cool.
- Some chips also report the speed the board **asks** for (LHM "Control" sensors, in %). It is a
  reading, not a command, but many chips and most laptops don't report it.
- Header names (CPU_FAN, SYS_FAN1) are rarely known: usually just "Fan #1", "Fan #2".
- A program can't send a test pulse: the tachometer only goes from the fan to the board.
  WyreMonitor only reads, so it never spins fans up to test them.

The rules must work on any PC, so they can't depend on Control sensors, header names or fan
control. Those are only extra hints where they exist.

## A header is empty until it gets a signal

1. **Empty by default.** A header that has never had a signal is drawn as an empty header, its
   pins, with "No signal". No stop alert is possible on it: the option isn't offered.
2. **A signal makes it a fan, and it stays one.** Two readings in a row above 0 RPM (one odd
   reading is not a signal; values of 30,000 RPM and more are not real fans) make the header a
   fan. The service remembers it, across restarts: a fan that stops on purpose is still drawn as
   a fan at 0 RPM, also right after Windows starts.
3. **Periodic check: a fan with no signal for 100 hours of use goes back to empty.** The service
   counts only the time it actually watched the sensors (while the PC is on and WyreMonitor is
   reading), never calendar days, so a month away from the PC changes nothing. 100 hours is about
   two to three weeks of normal use: long enough for fans that rest a lot (a chipset fan in
   winter) not to come and go. A new signal makes it a fan again at once.
4. **A fan with "Alert me if it stops" on never goes back to empty by itself.** Its alert has been
   showing all that time; hiding it would hide a possible fault. Only the user decides.
5. **By hand:** "Back to empty header" in the fan's detail panel. It also turns its alert off.
   A later signal makes it a fan again.

## Alerts

- **Off by default** on every fan: a fan that stops shows "0 RPM", nothing else.
- **"Alert me if it stops"**, per fan, only on fans (never on an empty header), like the BIOS
  "Fan Fail Warning": a fan with it on that reads 0 RPM for three readings in a row is an alert
  ("Stopped", in red, and a Windows notification if those are on).
- The global setting "A fan that stops" stays as the master switch.

## The detail panel of a header at 0 RPM

Empty header:

```
No fan seen on this header
  It becomes a fan as soon as it gets a signal, and stays one.
  The board asks it for 66 % and reads 0 RPM.     (only when the chip reports it)
  To be sure: the fan page of your BIOS lists every header by name.
  [ Hide it from the board ]
```

Fan standing still:

```
[ ] Alert me if Fan A4 stops
Standing still
  Last signal: 2 hours ago. No signal for 2 of the 100 hours of use after which
  it goes back to an empty header (never while its alert is on).
  The board asks it for 0 %: it is off on purpose.  (only when the chip reports it)
  [ Back to empty header ]
```

## Where things live

- The **service** keeps, for each header that has had a signal: when it last had one, and how
  many watched hours have passed since. In HKLM\SOFTWARE\WyreMonitor (value `Fans`), which only
  administrators and the service can change; removed on uninstall. It reports these facts in
  `fanSignals` and never applies the rules itself: it stays read-only towards the hardware and
  takes no requests that change anything.
- The **page** (and the tray icon's alerts) apply rules 1–5 from those facts and the user's
  choices: `watchFans` (alerts) and `forgotFans` (when "Back to empty header" was pressed, kept
  per user; a signal after that time makes it a fan again).

## Not doing

- Guessing from patterns of behaviour ("it stops and restarts, so it has a stop mode").
- Spinning fans up to test them (needs writing to the hardware; WyreMonitor only reads).
- A "this fan stopped" notice for fans without "Alert me if it stops".
