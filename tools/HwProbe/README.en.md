# HwProbe — M0 hardware check

> English version of [`README.md`](README.md) (中文). The two files are kept in sync.

Reads raw G29 input and drives force feedback through SDL3. Gate (design doc): **the clutch is an independent analog axis, and SDL3 can drive G29 force feedback.**

## Preparation

1. Connect the G29 to USB and power; set the mode switch on the back to the PC position (PS3).
2. Install Logitech G HUB and, in the G29 settings, **turn off "Combined Pedals"**; set force-feedback strength to 100 % and turn off the centering spring.
3. Build: `dotnet build tools/HwProbe`

Run: `dotnet run --project tools/HwProbe -- <command>` (written as `HwProbe <command>` below).

## Steps

### 1. `HwProbe list`

Note: name, VID/PID, `path`, axis/button/hat counts, the `haptic` line and the `effects` line.

### 2. `HwProbe watch --csv watch.csv`

In order, noting which row moves at each step:

1. Clutch only: press slowly to the floor, release slowly, 3 times. **Exactly one axis may move**; the throttle and brake axes stay still.
2. Same for throttle only, then brake only.
3. Press all three pedals at random for a while to confirm they do not interfere.
4. Shift through 1, 2, 3, 4, 5, 6, R (push the lever down for reverse); note each gear's button number under `recent presses`.
5. Press each wheel button in turn (note the order); the D-pad shows on the `hat` row.

Press `q` to quit and paste the final `Summary` block; keep `watch.csv` (used to analyse quantisation steps and noise).
`poll` at the top should be close to 1000 Hz; `input changes` is the device's real report rate while a pedal moves.

### 3. `HwProbe ffb`

Hold the wheel lightly. First an automatic sweep of 18 steps (2 s each); rate each step 0–3 for strength. Then interactive mode:

- Up/down changes frequency, left/right magnitude: find the smallest magnitude you can feel around 27 Hz (idle firing frequency).
- `m` turns on modulation: a new effect every 10 ms, simulating judder that follows load. **Is it smooth, or does it stutter?**
- `g` grind buzz, `s` stall jolt: press each a few times and say whether they feel right.
- Does `failed` stay at 0, and what is `call time max`?

### 4. Run again with the other backend

By default SDL lets its HIDAPI LG4FF driver take over the G29. Use `--lg4ff off` to switch to DirectInput (G HUB driver) and run
`list` and `ffb` again to compare which backend works and whether the feel differs.

## Report template

```
list (default backend): <full output>
list (--lg4ff off): <full output>
watch Summary: <full output>
Clutch/throttle/brake are axis ?, ?, ?; pedals independent: yes/no
Gear buttons: 1=? 2=? 3=? 4=? 5=? 6=? R=?
Wheel buttons: <order pressed → button numbers>
ffb sweep feel (0–3): 10Hz ?/?/?  20Hz ?/?/?  27Hz ?/?/?  40Hz ?/?/?  60Hz ?/?/?  100Hz ?/?/?
Modulation smooth:  failed:  call time max:
Grind / stall feel:
Difference between backends:
```
