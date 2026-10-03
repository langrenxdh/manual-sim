# Installing Manual Sim

[中文](install.md)

A manual transmission practice simulator: clutch, throttle and gear changes in a 2019 VW Golf 110TSI (and two other cars), on a hill road and a small town with steering.

## What you need

- Windows 10 or 11, 64-bit. No .NET install is needed: the package carries its own runtime.
- A Logitech G29 (wheel, three pedals) with the H-shifter, plugged in, switch in PS3 mode, and Logitech G HUB installed. The pedal and button layout in `config/g29.json` is for the G29; other wheels are not supported yet.
- Speakers or headphones (the engine sound matters for clutch control).

## Install

1. Unzip `ManualSim.zip` anywhere you can write to (for example `Documents\ManualSim`). Scores and recordings are saved next to `config/`, so not `Program Files`.
2. In G HUB set the G29's operating range to 900° and turn off its own centring spring (the simulator controls the force feedback).
3. Run `ManualSim.exe`. The first time, a short setup asks for your screen width, your viewing distance and checks your speakers.

## Keys

| Key | Does |
| --- | --- |
| E | Exercises, exams and progress |
| R / H | Restart (H: on the hill) or retry the exercise |
| M | Hill road / town map (steering) |
| V | Choose the car |
| T | Teaching mode (clutch curve, cues) |
| P | Replay |
| C | Air-con |
| L | Chinese / English |
| Tab | Tuning panel |
| F2 | First-time setup again |
| F11 | Borderless full screen |
| Esc | Leave the exercise, or quit |

Start the engine with X on the wheel. Reverse is the H-shifter's 7th position.

## Building the package (for developers)

From the repository: `pwsh scripts/publish.ps1`. The result is `publish/ManualSim/` and `publish/ManualSim.zip`.
