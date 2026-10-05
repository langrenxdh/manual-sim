# Engineering rules

[中文](engineering-rules.md)

The ground rules for working on this codebase. The full specification is in [`design.en.md`](design.en.md); the plan after v1 is in [`roadmap.en.md`](roadmap.en.md).

## Stack

- C# / .NET 10. The app (`Sim.App`, `Sim.Input`, `HwProbe`) targets Windows;
  `Sim.Core`, `Sim.Training` and their tests build and pass on Linux too (`net10.0`, nothing OS-specific).
- raylib-cs for rendering and audio streaming.
- SDL3 (C# bindings) for joystick input and haptic force feedback only, never for windowing.
- xUnit v3 for tests (`global.json` opts `dotnet test` into Microsoft.Testing.Platform, required on .NET 10).

## Project layout

```
src/Sim.Core          physics core, zero dependencies
src/Sim.Input         SDL3 input adapter + force feedback
src/Sim.App           raylib host: render, audio, tuning panel, telemetry
src/Sim.Training      exercises and scoring, pure, same rules as Sim.Core
tests/Sim.Core.Tests  acceptance + unit tests
tests/Sim.Training.Tests  scripted exercise and scoring tests
tools/HwProbe         hardware probe for the G29
config/               vehicle parameter files (e.g. golf-110tsi.json)
```

## Hard rules

1. **`Sim.Core` has no dependencies** on SDL, raylib, file IO, threads or clocks.
   Input struct in, state struct out, fixed step dt = 1 ms.
2. **Behaviour must emerge from physics.** No special-case code for stalling, shudder,
   hill starts or "lugging". If a behaviour is wrong, fix the model, not with an if-statement.
3. **No magic numbers in physics.** Every tunable lives in the vehicle parameter record,
   loaded from `config/*.json`, and is exposed in the tuning panel.
4. **The acceptance tests (T1–T20) and scoring tests (S1–S13) in the design doc must always pass.**
   Never weaken, delete or loosen a test to make it pass.
5. **Hardware is fixed:** G29 wheel + 3 pedals + H-shifter, one monitor, ordinary PC speakers.
   Nothing may need extra hardware.
6. **Physics never waits for rendering.** The physics thread at 1 kHz writes a lock-free double
   buffer; render, audio callback, force feedback (~100 Hz) and telemetry each read the latest state.
7. **One milestone at a time** (M0 → M4 in the design doc, then M5 → M13 in the roadmap, in that order).
   Each milestone ends at its gate, with a hand check before the next one starts.

## Conventions

- Code, identifiers and comments are in English.
- Human-facing docs are bilingual: Chinese `X.md` plus English `X.en.md`, each linking to the other.
  Every doc change goes into both files in the same commit.
  The one exception is the front page: `README.md` is English and `README.zh.md` is Chinese.
- Small commits, one concern each.
- Anything that needs the physical G29 (hardware checks, feel tuning) is verified by hand on the wheel.
