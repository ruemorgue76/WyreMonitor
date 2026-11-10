# Fans: what WyreMonitor shows and when it warns

Status: proposal, agreed in principle with the maintainer on 10 November 2026. Not released yet.

## What the hardware tells us

A fan header has a tachometer wire: the fan pulls it to ground twice per turn, and the
motherboard's Super I/O chip turns the time between pulses into RPM. That is the only reading.

- No pulses means 0 RPM. A fan standing still, an empty header, a broken fan and an unplugged
  cable all read exactly the same. No common chip can tell whether something is connected.
- Many fans stop on purpose: almost every graphics card, many chipset fans, and any fan the BIOS
  "Fan Stop" setting turns off while things are cool.
- Some chips also report the speed the board **asks** for (LHM "Control" sensors, in %). It is a
  reading too, not a command, but many chips and most laptops don't report it.
- Header names (CPU_FAN, SYS_FAN1) are rarely known: usually just "Fan #1", "Fan #2".
- A program can't send a test pulse: the tachometer only goes from the fan to the board. The only
  active test is to spin the fan up and watch for pulses, which needs writing to the hardware.
  WyreMonitor only reads, so it doesn't do that.

The design must work on any PC, so it can't depend on Control sensors, header names or fan
control. It uses them only as extra hints where they exist.

## Rules

1. **Show only what the sensors read now.** Every header the chip reports is drawn as a fan with
   its reading: its RPM, or "0 RPM". The program never says "empty" or "missing", because it
   can't know. No hidden memory of earlier readings decides how a fan is drawn.
2. **No fan alert by default.** A fan that stops shows "0 RPM", nothing else.
3. **The user says what the hardware can't**, per fan, in its detail panel:
   - *Alert me if it stops* (like the BIOS "Fan Fail Warning", off by default): a fan with it on
     that reads 0 RPM for three readings in a row is an alert ("Stopped", in red, and a Windows
     notification if those are on). Three readings: one odd reading is not a stop.
   - *It's empty, hide it*: the existing "Hide from the board", which can always be undone.
4. The global setting "A fan that stops" stays as the master switch for these alerts.

## Helping the user tell an empty header from a fan

In the detail panel of a fan reading 0 RPM, a short box puts the clues in order. The user decides.

```
Is anything connected here?
  Highest since Windows started: 0 RPM. It hasn't turned since then.
  The board asks for: 40 %                 (only when the chip reports it)
  A header that stays at 0 RPM even while the PC is busy is probably empty.
  The fan page in your BIOS lists every header by name.
  [ It's empty, hide it ]   [ Alert me if it stops ]
```

- **Highest since Windows started**: the sensor's maximum as LibreHardwareMonitor keeps it since
  the service started (`max` in the API, already shown in the list view). Above 0 means a fan is
  there and has turned; 0 for hours is a strong hint of an empty header. It starts again from 0
  whenever the service restarts: when Windows starts, and also after a WyreMonitor update. The
  box says how long it has been watching ("in the last 20 minutes"), so a short time isn't
  mistaken for proof.
- **The board asks for**: the Control sensor of the same header (same chip, same index:
  `/lpc/<chip>/0/control/N` with `/lpc/<chip>/0/fan/N`). "Asks 0 %" means stopped on purpose;
  "asks 40 %, reads 0" means empty or not responding. Left out when the chip doesn't report it.
- **The practical tip** works on every PC: fans that stop by themselves start when the PC works hard.
- **Where to be sure**: the BIOS fan page, the motherboard manual (which header is "Fan #4"), or a
  look inside the case.

## Not doing

- Guessing "empty socket" from how a header behaved before.
- Remembering fans across restarts to decide what to show or when to warn.
- Spinning fans up to test them (needs writing to the hardware; WyreMonitor only reads).
- A "this fan stopped" notice for fans without *Alert me if it stops* (decided: show only 0 RPM).
