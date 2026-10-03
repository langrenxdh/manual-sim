using System.Diagnostics;
using SDL;
using static SDL.SDL3;

namespace Sim.Input;

/// <summary>
/// G29 wheel motor through SDL3 haptics: a continuously updated sine (engine judder), a second sine
/// switched on and off (gear grinding), short constant-force pulses (jolts) and an optional
/// centering spring. Create, use and dispose it on one thread (the force-feedback thread); it opens
/// the haptic device by name, independently of the joystick the physics thread polls.
/// </summary>
public sealed unsafe class ForceFeedback : IDisposable
{
    private readonly SDL_Haptic* _haptic;
    private readonly SDL_HapticEffectID _shudder, _grind, _jolt, _spring;
    private bool _shudderRunning, _grindRunning, _springRunning;

    public string Name { get; }
    public long Calls { get; private set; }
    public long Failures { get; private set; }
    public double MaxCallMs { get; private set; }
    public string? LastError { get; private set; }

    private ForceFeedback(SDL_Haptic* haptic, string name)
    {
        _haptic = haptic;
        Name = name;
        uint features = SDL_GetHapticFeatures(haptic);
        if ((features & SDL_HAPTIC_AUTOCENTER) != 0) SDL_SetHapticAutocenter(haptic, 0);

        var sine = Sine(30, 0, SDL_HAPTIC_INFINITY);
        _shudder = SDL_CreateHapticEffect(haptic, &sine);
        var grind = Sine(80, 0, SDL_HAPTIC_INFINITY);
        _grind = SDL_CreateHapticEffect(haptic, &grind);
        var jolt = Constant(0, 100);
        _jolt = SDL_CreateHapticEffect(haptic, &jolt);
        var spring = Spring(0);
        _spring = (features & SDL_HAPTIC_SPRING) != 0 ? SDL_CreateHapticEffect(haptic, &spring) : (SDL_HapticEffectID)(-1);
        if ((int)_shudder < 0 || (int)_grind < 0 || (int)_jolt < 0)
            throw new InvalidOperationException($"Creating haptic effects failed: {SDL_GetError()}");
    }

    /// <summary>Opens the first haptic device whose name contains <paramref name="nameContains"/>.</summary>
    public static ForceFeedback? Open(string nameContains, out string? error)
    {
        error = null;
        if (!SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_HAPTIC))
        {
            error = $"SDL haptic init failed: {SDL_GetError()}";
            return null;
        }
        using var ids = SDL_GetHaptics();
        for (int i = 0; ids != null && i < ids.Count; i++)
        {
            string? name = SDL_GetHapticNameForID(ids[i]);
            if (name is null || !name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) continue;
            var haptic = SDL_OpenHaptic(ids[i]);
            if (haptic == null)
            {
                error = $"SDL_OpenHaptic failed: {SDL_GetError()}";
                continue;
            }
            try { return new ForceFeedback(haptic, name); }
            catch (InvalidOperationException ex)
            {
                SDL_CloseHaptic(haptic);
                error = ex.Message;
            }
        }
        error ??= $"no haptic device matching \"{nameContains}\"";
        SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_HAPTIC);
        return null;
    }

    /// <summary>Overall strength, 0..1.</summary>
    public void SetGain(double gain)
    {
        if ((SDL_GetHapticFeatures(_haptic) & SDL_HAPTIC_GAIN) != 0)
            Check(SDL_SetHapticGain(_haptic, (int)Math.Round(Math.Clamp(gain, 0, 1) * 100)));
    }

    /// <summary>Engine judder: sine at the firing frequency. Magnitude 0..1; 0 stops it.</summary>
    public void SetShudder(double frequencyHz, double magnitude)
    {
        SetSine(_shudder, ref _shudderRunning, frequencyHz, magnitude);
    }

    /// <summary>Gear grinding buzz. Magnitude 0..1; 0 stops it.</summary>
    public void SetGrind(double frequencyHz, double magnitude)
    {
        SetSine(_grind, ref _grindRunning, frequencyHz, magnitude);
    }

    /// <summary>One short constant-force pulse. Level -1..1 (sign = direction on the wheel).</summary>
    public void Jolt(double level, uint lengthMs)
    {
        var e = Constant(level, lengthMs);
        long t = Stopwatch.GetTimestamp();
        Done(t, SDL_UpdateHapticEffect(_haptic, _jolt, &e) && SDL_RunHapticEffect(_haptic, _jolt, 1));
    }

    /// <summary>Centering spring, coefficient 0..1; 0 (or no spring support) turns it off.</summary>
    public void SetSpring(double coefficient)
    {
        if ((int)_spring < 0) return;
        bool on = coefficient > 0;
        if (on)
        {
            var e = Spring(coefficient);
            long t = Stopwatch.GetTimestamp();
            Done(t, SDL_UpdateHapticEffect(_haptic, _spring, &e));
        }
        if (on != _springRunning)
        {
            long t = Stopwatch.GetTimestamp();
            Done(t, on ? SDL_RunHapticEffect(_haptic, _spring, 1) : SDL_StopHapticEffect(_haptic, _spring));
            _springRunning = on;
        }
    }

    private void SetSine(SDL_HapticEffectID id, ref bool running, double frequencyHz, double magnitude)
    {
        bool on = magnitude > 0 && frequencyHz > 0;
        if (on)
        {
            var e = Sine(frequencyHz, magnitude, SDL_HAPTIC_INFINITY);
            long t = Stopwatch.GetTimestamp();
            Done(t, SDL_UpdateHapticEffect(_haptic, id, &e));
        }
        if (on != running)
        {
            long t = Stopwatch.GetTimestamp();
            Done(t, on ? SDL_RunHapticEffect(_haptic, id, 1) : SDL_StopHapticEffect(_haptic, id));
            running = on;
        }
    }

    /// <summary>Records one SDL call started at <paramref name="startTimestamp"/>.</summary>
    private void Done(long startTimestamp, bool ok)
    {
        double ms = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        Calls++;
        if (ms > MaxCallMs) MaxCallMs = ms;
        Check(ok);
    }

    private void Check(bool ok)
    {
        if (ok) return;
        Failures++;
        LastError = SDL_GetError();
    }

    private static SDL_HapticDirection SteeringAxis()
    {
        SDL_HapticDirection d = default;
        d.type = SDL_HapticDirectionType.SDL_HAPTIC_STEERING_AXIS;
        return d;
    }

    private static SDL_HapticEffect Sine(double frequencyHz, double magnitude, uint lengthMs)
    {
        SDL_HapticEffect e = default;
        e.type = SDL_HapticEffectType.SDL_HAPTIC_SINE;
        e.periodic.direction = SteeringAxis();
        e.periodic.length = lengthMs;
        // The period is whole milliseconds, so high frequencies are quantised (100 Hz = 10 ms).
        e.periodic.period = (ushort)Math.Clamp((int)Math.Round(1000 / Math.Max(frequencyHz, 1e-3)), 1, ushort.MaxValue);
        e.periodic.magnitude = (short)Math.Round(Math.Clamp(magnitude, 0, 1) * short.MaxValue);
        return e;
    }

    private static SDL_HapticEffect Constant(double level, uint lengthMs)
    {
        SDL_HapticEffect e = default;
        e.type = SDL_HapticEffectType.SDL_HAPTIC_CONSTANT;
        e.constant.direction = SteeringAxis();
        e.constant.length = lengthMs;
        e.constant.level = (short)Math.Round(Math.Clamp(level, -1, 1) * short.MaxValue);
        return e;
    }

    private static SDL_HapticEffect Spring(double coefficient)
    {
        SDL_HapticEffect e = default;
        e.type = SDL_HapticEffectType.SDL_HAPTIC_SPRING;
        e.condition.direction = SteeringAxis();
        e.condition.length = SDL_HAPTIC_INFINITY;
        short coeff = (short)Math.Round(Math.Clamp(coefficient, 0, 1) * short.MaxValue);
        e.condition.right_sat[0] = e.condition.left_sat[0] = ushort.MaxValue;
        e.condition.right_coeff[0] = e.condition.left_coeff[0] = coeff;
        return e;
    }

    public void Dispose()
    {
        SDL_StopHapticEffects(_haptic);
        SDL_CloseHaptic(_haptic);
        SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_HAPTIC);
    }
}
