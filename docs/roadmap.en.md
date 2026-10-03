# Roadmap after v1

2026-10-03 · Donghui

> English version of [`roadmap.md`](roadmap.md) (中文). The two files are kept in sync.

v1 (M0–M3) is complete, and M4 (graded exercises and scoring) is built. This document collects every idea for what comes next (all of them accepted), and fixes the implementation order and the gate for each milestone.

**How this relates to the design doc.** This is the plan; [`design.en.md`](design.en.md) remains the specification. When a milestone starts, its detailed design (parameters, tests, decisions) is written into the design doc in the same way as M0–M4, and this roadmap only links to it. The working rules do not change: one milestone at a time, plan mode first, stop at the gate.

## All ideas

| # | Idea | Group |
| --- | --- | --- |
| 1 | Traffic-light queue: creep, stop and go behind a car | More practice without steering |
| 2 | Chained hill sequence: approach, stop on the line, hill start | More practice without steering |
| 3 | Downhill (engine braking, gear choice) and reversing uphill | More practice without steering |
| 4 | Rev-matched downshifts (throttle blip) | More practice without steering |
| 5 | Ghost comparison: your best attempt's traces over the current one in the replay | Coaching |
| 6 | Progress view: score history per exercise | Coaching |
| 7 | Exam mode: a fixed sequence with an overall pass or fail | Coaching |
| 8 | Audio cues in teaching mode ("clutch at the bite", "more throttle") | Coaching |
| 9 | Steering and lateral dynamics; roads with bends, intersections, roundabouts | Steering (v2 headline) |
| 10 | Steering force feedback: self-centring and road feel, judder layered on top | Steering |
| 11 | Steering exercises: roundabout, car park, three-point turn | Steering |
| 12 | Clutch temperature: a hot clutch fades and smells (warning) | Fidelity |
| 13 | Cold engine (higher, less stable idle) and air-con load at idle | Fidelity |
| 14 | Other cars: more vehicle configs and a car menu | Fidelity |
| 15 | Sharing: installer, setup for other wheels and pedals, Chinese/English UI | Sharing |

## Order and reasoning

1. **Finish M4 first.** It is almost done and the exercise framework everything else builds on.
2. **Coaching tools before more exercises (M5).** The ghost comparison and the progress view make *every* exercise more useful, including the ones that follow, and they only use data we already record.
3. **New exercises without steering (M6)** reuse the straight road, the exercise framework and the scoring. They need small additions only: a lead car, a downhill section, a reverse start.
4. **Fidelity (M7)** after the exercises, because clutch temperature and cold engines change how the exercises feel and score. Better to calibrate the scores once, on the more realistic model. Other cars come here because the car menu builds on the same per-car tuning.
5. **Exam mode and audio cues (M8)** need the full set of no-steering exercises to draw from.
6. **Steering (M9) last among the big items, not first.** It is the largest change: a new input, a lateral physics model, a new road network and a different force-feedback design. Everything before it delivers value quickly and none of it is wasted when steering arrives. It starts with a short hardware check, like M0.
7. **Sharing (M10)** only makes sense once the product is complete, and it requires lifting docs/engineering-rules.en.md hard rule 5 ("hardware is fixed"). That is your decision to make at that point; until then rule 5 stands.

## Milestones

### M4 close-out — graded exercises and scoring

- [x] Exercises finish in the target gear or higher (fixed; previously they could hang until the time limit).
- [x] The right-hand box sometimes disappears: the known case (the result card covering the right teaching box) is fixed; in a too-narrow view the two teaching boxes now stack on the left.
- [x] Gate: scores rank attempts the way you would, and the main deduction names the real mistake (accepted).
- [ ] Push and open a PR.

### M5 — Coaching I: ghost comparison and progress (ideas 5, 6)

**Status: implemented (2026-10-03), see the M5 decision record in the design doc; hand check pending.**

- **Ghost:** for each exercise, the telemetry of the best completed attempt is saved (`scores/ghosts/<exercise>.bin`, same format as telemetry). The replay draws its pedal, rpm and speed traces faintly behind the current attempt, aligned at the attempt start. A new best replaces the ghost.
- **Progress view:** a screen from the menu that plots each exercise's scores over time from `scores/scores.jsonl`, with the best and the trend, and the metric that most often costs points.
- No physics change. Pure logic (alignment, trend, "most costly metric") goes into `Sim.Training` with tests.
- **Gate:** after a bad attempt, the ghost shows clearly where you differed from your best; the progress view matches your sense of how you are doing.

### M6 — More practice without steering (ideas 1, 2, 3, 4)

**Status: implemented (2026-10-03), see the M6 decision record in the design doc; hand check pending.**

- **Traffic-light queue (1):** a scripted lead car (kinematic, not physics) that creeps, stops and goes in a stop-and-go pattern; a traffic light at the end of the queue. Scored on stalls, gaps that are too small or too large, clutch heat and lurch. Needs a lead car in the scene and a distance metric.
- **Chained hill sequence (2):** start on the flat, drive to the hill, stop with the front of the car within a band before the stop line, then hill-start. Scored on the stop position, then as the existing hill start. Needs the car's front position relative to the line.
- **Downhill and reversing uphill (3):** the scene gains a downhill section after the plateau. Downhill exercise: hold a speed limit with engine braking in a sensible gear, little foot brake. Reverse uphill: start facing downhill at the bottom of a slope and reverse up it. Needs a scene extension and a reverse start; the physics already supports reverse and negative grade.
- **Rev-matched downshifts (4):** from about 50 km/h in 3rd, downshift to 2nd; scored on lurch (peak jerk at clutch engagement), clutch heat and engine speed matching at engagement.
- New metrics in `Sim.Training` with scripted tests (like S1–S5); the scene changes are in `scene.json` and the 3D view.
- **Gate:** each new exercise feels like the real situation and its score is fair, in the same sense as M4.

### M7 — Fidelity and other cars (ideas 12, 13, 14)

- **Clutch temperature (12):** a thermal state driven by slip power (the slip energy we already measure) with cooling. A hot clutch's friction coefficient falls (fade: less capacity, more slip); above a threshold a warning appears ("clutch smell"). It emerges from physics, no special rule (hard rule 2). Parameters in the vehicle file and panel.
- **Cold engine and air-con (13):** engine temperature state; cold friction is higher and the ECU idle target is higher, settling to normal idle as the engine warms (the values are estimates to tune by feel against my own car). Air-con as a switchable load torque at idle that the idle controller must cover.
- **Other cars (14):** more `config/*.json` vehicle files (for example a small naturally aspirated petrol, a diesel with high low-end torque, a heavy car) and a car menu. Without real data, each is tuned by plausibility and published figures.
- Physics changes go into Sim.Core with **new acceptance tests T6+** (for example: a long slip at high rpm fades the clutch; a cold idle is higher and settles as it warms). **T1–T5 must stay green.** If a new effect conflicts with them, stop and discuss, as always.
- After the changes, recalibrate the M4/M6 scoring tables and check S1–S5 still hold.
- **Gate:** the effects are noticeable but not exaggerated; the Golf still feels like the Golf; another car feels clearly different.

### M8 — Coaching II: exam mode and audio cues (ideas 7, 8)

- **Exam mode (7):** a fixed sequence (for example: pull-away, chained hill sequence, queue, downshift, reverse uphill), each part scored; overall pass if every part completes and the total reaches a threshold. Results in the history. Pure logic in `Sim.Training` with tests.
- **Audio cues (8):** in teaching mode only, short spoken or tone cues triggered by state: approaching the bite point, idle control nearly exhausted ("more throttle"), rollback starting. Uses pre-recorded or synthesised clips through raylib audio; the rules are thresholds in a config file.
- **Gate:** the exam feels like a fair test; the cues help without nagging.

### M9 — Steering (v2 headline; ideas 9, 10, 11)

Split into steps, each with its own gate:

- **M9a hardware check (like M0):** read the wheel angle at 1 kHz (axis 0 already reads, 12k levels over the range); verify a constant-force effect can be updated at 100+ Hz to produce a smooth, variable centring torque without stutter, together with the judder sine. `HwProbe` gets a steering-torque test. Gate: smooth variable torque on the G29.
- **M9b lateral physics (Sim.Core; can run in the cloud):** a single-track ("bicycle") model with a simple tyre model (linear up to the grip limit, then saturating), yaw rate, lateral acceleration, the steering ratio and the self-aligning torque. New inputs: steering angle. New acceptance tests (for example: steady circle radius matches the steering angle at low speed; grip limit reached at a plausible lateral g). The longitudinal model and T1–T5 are unchanged.
- **M9c roads:** the straight road becomes a road network: bends, a T intersection, a roundabout, a car park. A centre-line path with curvature; the 3D view, camera yaw and the physics share it. Leaving the road is detected (no crash physics in v2).
- **M9d steering force feedback:** the wheel torque comes from the model's self-aligning torque plus a centring spring, with engine judder layered on top; replaces the "spring off" default. Tuned by feel.
- **M9e steering exercises:** roundabout (slow down, 2nd gear, give way, pull away while turning), car-park bay, three-point turn; scored like the others plus line keeping.
- **Gate (M9 overall):** driving through the roundabout and the car park feels natural on the G29, and the longitudinal feel of v1 is unchanged.

### M10 — Sharing (idea 15; needs your decision to lift hard rule 5)

- Installer (self-contained .NET publish + config and setup on first run).
- Input setup wizard for other wheels and pedals: detect axes and buttons by asking the user to press each one (generalising `HwProbe watch`), write a `config/<device>.json`; force feedback optional.
- Chinese/English UI: string tables, font with CJK glyphs.
- **Gate:** a friend installs it on their own PC and wheel and drives without help.

## Notes for every milestone

- Bilingual docs and docs/engineering-rules.en.md rules apply throughout: tests are never weakened, every tunable goes in a config file and the panel, physics never waits.
- Pure logic (Core, Training) can be done in cloud sessions; anything touching the G29, the screen or sound must be local (docs/engineering-rules.en.md).
- Each milestone ends with a hand check on the G29 and stops at its gate.
