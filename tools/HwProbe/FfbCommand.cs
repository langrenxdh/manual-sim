using System.Diagnostics;
using System.Text;
using SDL;
using static SDL.SDL3;

namespace HwProbe;

/// <summary>
/// `HwProbe ffb`: drives the wheel motor through SDL3 haptics.
/// Phase 1 sweeps a sine effect over frequency x magnitude; phase 2 is interactive and updates the
/// running effect at 100 Hz, the rate the M3 force-feedback thread will use for engine shudder.
/// Also offers a short high-frequency burst (gear grind) and a constant jolt (stall).
/// </summary>
internal static unsafe class FfbCommand
{
    private static readonly int[] SweepFrequenciesHz = [10, 20, 27, 40, 60, 100];
    private static readonly double[] SweepMagnitudes = [0.25, 0.5, 1.0];
    private const double UpdatePeriodMs = 10.0; // 100 Hz, as planned for M3
    private const double ModulationHz = 0.5;
    private const int GrindPeriodMs = 8;        // ~125 Hz buzz
    private const uint GrindLengthMs = 300;
    private const double GrindMagnitude = 0.6;
    private const uint JoltLengthMs = 120;
    private const double JoltLevel = 0.8;

    private static readonly (SDL_HapticDirectionType type, string name)[] Directions =
    [
        (SDL_HapticDirectionType.SDL_HAPTIC_STEERING_AXIS, "steering-axis"),
        (SDL_HapticDirectionType.SDL_HAPTIC_CARTESIAN, "cartesian x"),
        (SDL_HapticDirectionType.SDL_HAPTIC_POLAR, "polar 90deg"),
    ];

    public static int Run(Options opts, CancellationToken cancel)
    {
        var joy = Sdl.OpenJoystick(opts.DeviceIndex);
        if (joy == null) return 1;
        SDL_Haptic* haptic = null;
        try
        {
            if (!SDL_IsJoystickHaptic(joy))
            {
                Console.WriteLine($"FAIL: SDL reports no haptic support on this joystick ({SDL_GetError()}).");
                Console.WriteLine("Try the other backend: --lg4ff on / --lg4ff off.");
                return 1;
            }
            haptic = SDL_OpenHapticFromJoystick(joy);
            if (haptic == null)
            {
                Console.WriteLine($"FAIL: SDL_OpenHapticFromJoystick: {SDL_GetError()}");
                return 1;
            }
            return RunWithHaptic(haptic, opts, cancel);
        }
        finally
        {
            if (haptic != null)
            {
                SDL_StopHapticEffects(haptic);
                SDL_CloseHaptic(haptic);
            }
            SDL_CloseJoystick(joy);
            Console.CursorVisible = true;
        }
    }

    private static int RunWithHaptic(SDL_Haptic* haptic, Options opts, CancellationToken cancel)
    {
        uint features = SDL_GetHapticFeatures(haptic);
        Console.WriteLine($"haptic effects: {Sdl.FeatureNames(features)}");

        if ((features & SDL_HAPTIC_GAIN) != 0)
            Report("set gain 100%", SDL_SetHapticGain(haptic, 100));
        if ((features & SDL_HAPTIC_AUTOCENTER) != 0)
            Report("disable autocenter", SDL_SetHapticAutocenter(haptic, 0));

        if ((features & SDL_HAPTIC_SINE) == 0)
        {
            Console.WriteLine("FAIL: no sine effect. Trying plain rumble for 1 s instead...");
            bool ok = SDL_InitHapticRumble(haptic) && SDL_PlayHapticRumble(haptic, 0.5f, 1000);
            Report("rumble 50% for 1 s", ok);
            Thread.Sleep(1200);
            return 1;
        }

        int dirIndex = -1;
        SDL_HapticEffectID sine = default;
        for (int d = 0; d < Directions.Length; d++)
        {
            var e = Sine(Directions[d].type, 27, 0.5, SDL_HAPTIC_INFINITY);
            sine = SDL_CreateHapticEffect(haptic, &e);
            if ((int)sine >= 0) { dirIndex = d; break; }
            Console.WriteLine($"  sine with {Directions[d].name} direction rejected: {SDL_GetError()}");
        }
        if (dirIndex < 0)
        {
            Console.WriteLine("FAIL: could not create a sine effect with any direction type.");
            return 1;
        }
        var dir = Directions[dirIndex].type;
        Console.WriteLine($"sine effect created (direction: {Directions[dirIndex].name})");

        var grindEffect = Sine(dir, 1000.0 / GrindPeriodMs, GrindMagnitude, GrindLengthMs);
        var grind = SDL_CreateHapticEffect(haptic, &grindEffect);
        if ((int)grind < 0) Console.WriteLine($"  grind burst unavailable: {SDL_GetError()}");

        SDL_HapticEffectID jolt = (SDL_HapticEffectID)(-1);
        if ((features & SDL_HAPTIC_CONSTANT) != 0)
        {
            var joltEffect = Constant(dir, JoltLevel, JoltLengthMs);
            jolt = SDL_CreateHapticEffect(haptic, &joltEffect);
            if ((int)jolt < 0) Console.WriteLine($"  stall jolt unavailable: {SDL_GetError()}");
        }

        if (!SDL_RunHapticEffect(haptic, sine, 1))
        {
            Console.WriteLine($"FAIL: SDL_RunHapticEffect: {SDL_GetError()}");
            return 1;
        }
        Console.WriteLine("Hold the wheel lightly. Sweep starts in 2 s (any key skips a step, i = go to interactive, q = quit).");
        Thread.Sleep(2000);

        bool quit = Sweep(haptic, sine, dir, opts.StepSeconds, cancel);
        if (!quit && !opts.SweepOnly && !cancel.IsCancellationRequested)
            Interactive(haptic, sine, grind, jolt, dir, cancel);
        return 0;
    }

    /// <summary>Returns true if the user asked to quit.</summary>
    private static bool Sweep(SDL_Haptic* haptic, SDL_HapticEffectID sine, SDL_HapticDirectionType dir,
        double stepSeconds, CancellationToken cancel)
    {
        Console.WriteLine();
        Console.WriteLine("Sweep: note for each step whether you feel it, and how strong (0 = nothing .. 3 = strong).");
        int total = SweepFrequenciesHz.Length * SweepMagnitudes.Length, n = 0;
        foreach (int hz in SweepFrequenciesHz)
        {
            foreach (double mag in SweepMagnitudes)
            {
                n++;
                var e = Sine(dir, hz, mag, SDL_HAPTIC_INFINITY);
                bool ok = SDL_UpdateHapticEffect(haptic, sine, &e);
                Console.WriteLine($"  [{n,2}/{total}] {hz,3} Hz (period {PeriodMs(hz)} ms)  magnitude {mag * 100,3:F0}%" +
                                  (ok ? "" : $"  UPDATE FAILED: {SDL_GetError()}"));
                var until = Stopwatch.StartNew();
                while (until.Elapsed.TotalSeconds < stepSeconds)
                {
                    if (cancel.IsCancellationRequested) return true;
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true).Key;
                        if (key is ConsoleKey.Q or ConsoleKey.Escape) return true;
                        if (key == ConsoleKey.I) return false;
                        break;
                    }
                    Thread.Sleep(10);
                }
            }
        }
        return false;
    }

    private static void Interactive(SDL_Haptic* haptic, SDL_HapticEffectID sine, SDL_HapticEffectID grind,
        SDL_HapticEffectID jolt, SDL_HapticDirectionType dir, CancellationToken cancel)
    {
        double hz = 27, mag = 0.5;
        bool running = true, modulate = false;
        long updates = 0, failures = 0;
        double updateMsSum = 0, updateMsMax = 0;
        string lastError = "-", lastEvent = "-";

        Console.Clear();
        Console.CursorVisible = false;
        var clock = Stopwatch.StartNew();
        double nextUpdate = 0;
        bool dirty = true;

        while (!cancel.IsCancellationRequested)
        {
            while (Console.KeyAvailable)
            {
                switch (Console.ReadKey(intercept: true).Key)
                {
                    case ConsoleKey.Q or ConsoleKey.Escape: return;
                    case ConsoleKey.UpArrow: hz = Math.Min(hz + 1, 200); dirty = true; break;
                    case ConsoleKey.DownArrow: hz = Math.Max(hz - 1, 2); dirty = true; break;
                    case ConsoleKey.PageUp: hz = Math.Min(hz + 10, 200); dirty = true; break;
                    case ConsoleKey.PageDown: hz = Math.Max(hz - 10, 2); dirty = true; break;
                    case ConsoleKey.RightArrow: mag = Math.Min(mag + 0.05, 1); dirty = true; break;
                    case ConsoleKey.LeftArrow: mag = Math.Max(mag - 0.05, 0); dirty = true; break;
                    case ConsoleKey.M: modulate = !modulate; dirty = true; break;
                    case ConsoleKey.Spacebar:
                        running = !running;
                        bool ok = running ? SDL_RunHapticEffect(haptic, sine, 1) : SDL_StopHapticEffect(haptic, sine);
                        lastEvent = $"sine {(running ? "started" : "stopped")}{(ok ? "" : " FAILED")}";
                        if (!ok) lastError = SDL_GetError() ?? "?";
                        dirty = true;
                        break;
                    case ConsoleKey.G:
                        lastEvent = (int)grind < 0 ? "grind unavailable"
                            : SDL_RunHapticEffect(haptic, grind, 1) ? "grind burst" : $"grind FAILED: {SDL_GetError()}";
                        break;
                    case ConsoleKey.S:
                        lastEvent = (int)jolt < 0 ? "jolt unavailable"
                            : SDL_RunHapticEffect(haptic, jolt, 1) ? "stall jolt" : $"jolt FAILED: {SDL_GetError()}";
                        break;
                }
            }

            double now = clock.Elapsed.TotalMilliseconds;
            if (now < nextUpdate)
            {
                Thread.Sleep(1);
                continue;
            }
            nextUpdate = Math.Max(nextUpdate + UpdatePeriodMs, now);

            // In modulate mode every tick sends a new effect, like shudder following engine load.
            double effMag = mag, effHz = hz;
            if (modulate)
            {
                double phase = 0.5 + 0.5 * Math.Sin(2 * Math.PI * ModulationHz * now / 1000);
                effMag = mag * phase;
                effHz = hz * (0.75 + 0.5 * phase);
            }
            if (running && (dirty || modulate))
            {
                var e = Sine(dir, effHz, effMag, SDL_HAPTIC_INFINITY);
                long t0 = Stopwatch.GetTimestamp();
                bool ok = SDL_UpdateHapticEffect(haptic, sine, &e);
                double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                updates++;
                updateMsSum += ms;
                updateMsMax = Math.Max(updateMsMax, ms);
                if (!ok) { failures++; lastError = SDL_GetError() ?? "?"; }
                dirty = false;
            }

            Render(hz, mag, effHz, effMag, running, modulate, updates, failures, updateMsSum, updateMsMax,
                lastError, lastEvent);
        }
    }

    private static void Render(double hz, double mag, double effHz, double effMag, bool running, bool modulate,
        long updates, long failures, double updateMsSum, double updateMsMax, string lastError, string lastEvent)
    {
        var sb = new StringBuilder();
        int width = Math.Max(Console.WindowWidth - 1, 40);
        void Line(string s) => sb.Append(s.Length > width ? s[..width] : s.PadRight(width)).Append('\n');

        Line("Interactive force feedback (sine effect updated at 100 Hz)");
        Line("  up/down: freq +-1 Hz   PgUp/PgDn: +-10 Hz   left/right: magnitude +-5%");
        Line("  m: modulate (magnitude and freq swing at 0.5 Hz, new effect every 10 ms)");
        Line("  space: start/stop sine   g: grind burst   s: stall jolt   q: quit");
        Line("");
        Line($"sine:      {(running ? "running" : "stopped")}   modulate: {(modulate ? "on" : "off")}");
        Line($"setpoint:  {hz,5:F0} Hz   magnitude {mag * 100,3:F0}%");
        int period = PeriodMs(effHz);
        Line($"sent:      {effHz,5:F1} Hz -> period {period} ms (= {1000.0 / period:F1} Hz)   magnitude {effMag * 100,3:F0}%");
        Line($"updates:   {updates}   failed {failures}   " +
             $"call time avg {(updates == 0 ? 0 : updateMsSum / updates):F3} ms   max {updateMsMax:F3} ms");
        Line($"last event: {lastEvent}");
        Line($"last error: {lastError}");
        Console.SetCursorPosition(0, 0);
        Console.Write(sb.ToString());
    }

    private static int PeriodMs(double hz) => Math.Clamp((int)Math.Round(1000 / hz), 1, ushort.MaxValue);

    private static SDL_HapticEffect Sine(SDL_HapticDirectionType dir, double hz, double magnitude, uint lengthMs)
    {
        SDL_HapticEffect e = default;
        e.type = SDL_HapticEffectType.SDL_HAPTIC_SINE;
        e.periodic.direction = Direction(dir);
        e.periodic.length = lengthMs;
        e.periodic.period = (ushort)PeriodMs(hz);
        e.periodic.magnitude = (short)Math.Round(Math.Clamp(magnitude, 0, 1) * short.MaxValue);
        return e;
    }

    private static SDL_HapticEffect Constant(SDL_HapticDirectionType dir, double level, uint lengthMs)
    {
        SDL_HapticEffect e = default;
        e.type = SDL_HapticEffectType.SDL_HAPTIC_CONSTANT;
        e.constant.direction = Direction(dir);
        e.constant.length = lengthMs;
        e.constant.level = (short)Math.Round(Math.Clamp(level, -1, 1) * short.MaxValue);
        return e;
    }

    private static SDL_HapticDirection Direction(SDL_HapticDirectionType type)
    {
        SDL_HapticDirection d = default;
        d.type = type;
        switch (type)
        {
            case SDL_HapticDirectionType.SDL_HAPTIC_CARTESIAN: d.dir[0] = 1; break;
            case SDL_HapticDirectionType.SDL_HAPTIC_POLAR: d.dir[0] = 9000; break; // hundredths of a degree
        }
        return d;
    }

    private static void Report(string what, bool ok) =>
        Console.WriteLine($"{what}: {(ok ? "ok" : $"FAILED ({SDL_GetError()})")}");
}
