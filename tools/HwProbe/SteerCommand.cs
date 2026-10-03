using System.Diagnostics;
using SDL;
using static SDL.SDL3;

namespace HwProbe;

/// <summary>
/// `HwProbe steer` (M9a): can SDL3 give the G29 a smooth, continuously varying centring torque?
/// A constant-force effect is updated at 100 Hz with a virtual spring + damper computed from the wheel
/// angle (axis 0), while a small judder sine runs on top, as steering force feedback will in M9d.
/// Reports the update rate, failures and the slowest call.
/// </summary>
internal static unsafe class SteerCommand
{
    private const double UpdatePeriodMs = 10;
    private const double SpringPerFullLock = 0.8;   // force at full lock, fraction of maximum
    private const double DamperPerFullLockPerS = 0.15;
    private const double JudderHz = 30, JudderMagnitude = 0.08;
    private const int AngleAxis = 0;

    public static int Run(Options opts, CancellationToken cancel)
    {
        var joy = Sdl.OpenJoystick(opts.DeviceIndex);
        if (joy == null) return 1;
        SDL_Haptic* haptic = null;
        try
        {
            if (!SDL_IsJoystickHaptic(joy) || (haptic = SDL_OpenHapticFromJoystick(joy)) == null)
            {
                Console.WriteLine($"FAIL: no haptic device: {SDL_GetError()}");
                return 1;
            }
            uint features = SDL_GetHapticFeatures(haptic);
            if ((features & SDL_HAPTIC_CONSTANT) == 0)
            {
                Console.WriteLine("FAIL: the wheel does not support a constant-force effect.");
                return 1;
            }
            if ((features & SDL_HAPTIC_GAIN) != 0) SDL_SetHapticGain(haptic, 100);
            if ((features & SDL_HAPTIC_AUTOCENTER) != 0) SDL_SetHapticAutocenter(haptic, 0);

            var force = Constant(0);
            var forceId = SDL_CreateHapticEffect(haptic, &force);
            var judder = Sine(JudderHz, JudderMagnitude);
            var judderId = SDL_CreateHapticEffect(haptic, &judder);
            if ((int)forceId < 0 || (int)judderId < 0)
            {
                Console.WriteLine($"FAIL: creating effects: {SDL_GetError()}");
                return 1;
            }
            SDL_RunHapticEffect(haptic, forceId, 1);
            SDL_RunHapticEffect(haptic, judderId, 1);

            Console.WriteLine("Turn the wheel: it should pull back to centre smoothly, with a light buzz. q quits.");
            var clock = Stopwatch.StartNew();
            double next = 0, lastAngle = 0, lastT = 0, maxMs = 0, rateStart = 0;
            long updates = 0, failures = 0, inWindow = 0;
            double rate = 0;
            while (!cancel.IsCancellationRequested && (opts.Seconds <= 0 || clock.Elapsed.TotalSeconds < opts.Seconds))
            {
                if (!Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Q) break;
                double now = clock.Elapsed.TotalMilliseconds;
                if (now < next) { Thread.Sleep(1); continue; }
                next = Math.Max(next + UpdatePeriodMs, now);

                SDL_UpdateJoysticks();
                double angle = SDL_GetJoystickAxis(joy, AngleAxis) / 32768.0; // -1..1 of the lock
                double dt = Math.Max((now - lastT) / 1000, 1e-3);
                double rateOfTurn = (angle - lastAngle) / dt;
                lastAngle = angle;
                lastT = now;
                double level = Math.Clamp(-SpringPerFullLock * angle - DamperPerFullLockPerS * rateOfTurn, -1, 1);

                var e = Constant(level);
                long t0 = Stopwatch.GetTimestamp();
                bool ok = SDL_UpdateHapticEffect(haptic, forceId, &e);
                double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                maxMs = Math.Max(maxMs, ms);
                updates++;
                inWindow++;
                if (!ok) failures++;
                if (now - rateStart >= 1000)
                {
                    rate = inWindow / ((now - rateStart) / 1000);
                    inWindow = 0;
                    rateStart = now;
                    Console.WriteLine($"  angle {angle,6:F3}  force {level,6:F2}  rate {rate,5:F0} Hz  updates {updates}  failed {failures}  max call {maxMs:F2} ms" +
                                      (ok ? "" : $"  last error: {SDL_GetError()}"));
                }
            }
            Console.WriteLine($"Result: {updates} updates, {failures} failed, slowest call {maxMs:F2} ms.");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            if (haptic != null)
            {
                SDL_StopHapticEffects(haptic);
                SDL_CloseHaptic(haptic);
            }
            SDL_CloseJoystick(joy);
        }
    }

    private static SDL_HapticDirection Steering()
    {
        SDL_HapticDirection d = default;
        d.type = SDL_HapticDirectionType.SDL_HAPTIC_STEERING_AXIS;
        return d;
    }

    private static SDL_HapticEffect Constant(double level)
    {
        SDL_HapticEffect e = default;
        e.type = SDL_HapticEffectType.SDL_HAPTIC_CONSTANT;
        e.constant.direction = Steering();
        e.constant.length = SDL_HAPTIC_INFINITY;
        e.constant.level = (short)Math.Round(Math.Clamp(level, -1, 1) * short.MaxValue);
        return e;
    }

    private static SDL_HapticEffect Sine(double hz, double magnitude)
    {
        SDL_HapticEffect e = default;
        e.type = SDL_HapticEffectType.SDL_HAPTIC_SINE;
        e.periodic.direction = Steering();
        e.periodic.length = SDL_HAPTIC_INFINITY;
        e.periodic.period = (ushort)Math.Round(1000 / hz);
        e.periodic.magnitude = (short)Math.Round(magnitude * short.MaxValue);
        return e;
    }
}
