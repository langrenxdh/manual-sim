using SDL;
using Sim.Core;
using static SDL.SDL3;

namespace Sim.Input;

/// <summary>One poll of the wheel: what the physics gets, plus the pre-filter values for display.</summary>
public readonly record struct InputSample(
    DriverInput Input,
    bool Connected,
    double ClutchNormalised,
    double ThrottleNormalised,
    double BrakeNormalised,
    bool PedalsReported);

/// <summary>
/// Reads the G29 through SDL3 (joystick subsystem only, no window). Create, poll and dispose it on
/// one thread: SDL joystick calls must not be spread across threads.
/// </summary>
public sealed unsafe class G29Reader : IDisposable
{
    private SDL_Joystick* _joystick;
    private double _sinceReconnectS;
    private bool _handbrakeOn;
    private bool _handbrakeButtonWasDown;
    private readonly PedalChannel _clutch = new();
    private readonly PedalChannel _throttle = new();
    private readonly PedalChannel _brake = new();
    // Until the G29 sends its first report SDL returns 0 (mid travel) for every axis, and
    // SDL_GetJoystickAxisInitialState claims 0 is known (seen in M2). A pedal is therefore treated as
    // released until it reads anything other than 0.
    private bool _clutchReported, _throttleReported, _brakeReported;

    /// <summary>Mapping and filter settings. May be replaced between polls (tuning panel).</summary>
    public InputConfig Config { get; set; }

    /// <summary>Name of the connected wheel, or null.</summary>
    public string? DeviceName { get; private set; }

    public G29Reader(InputConfig config)
    {
        Config = config;
        // There is no SDL window to have focus; without this SDL may drop wheel input.
        SDL_SetHint(SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_JOYSTICK))
            throw new InvalidOperationException($"SDL_Init failed: {SDL_GetError()}");
        TryOpen();
    }

    public InputSample Poll(double dtS)
    {
        SDL_UpdateJoysticks();

        if (_joystick != null && !SDL_JoystickConnected(_joystick))
            Close();
        if (_joystick == null)
        {
            _sinceReconnectS += dtS;
            if (_sinceReconnectS >= Config.ReconnectIntervalS)
            {
                _sinceReconnectS = 0;
                TryOpen();
            }
            if (_joystick == null)
                return new InputSample(new DriverInput(0, 0, 0, Gear.Neutral), false, 0, 0, 0, false);
        }

        var c = Config;
        double clutch = _clutch.Update(c.Clutch, Axis(c.Clutch, ref _clutchReported), dtS);
        double throttle = _throttle.Update(c.Throttle, Axis(c.Throttle, ref _throttleReported), dtS);
        double brake = _brake.Update(c.Brake, Axis(c.Brake, ref _brakeReported), dtS);

        bool handbrakeDown = Button(c.HandbrakeButton);
        if (handbrakeDown && !_handbrakeButtonWasDown) _handbrakeOn = !_handbrakeOn;
        _handbrakeButtonWasDown = handbrakeDown;

        var input = new DriverInput(clutch, throttle, brake, Lever(c), _handbrakeOn, Button(c.StarterButton));
        return new InputSample(input, true, _clutch.Normalised, _throttle.Normalised, _brake.Normalised,
            _clutchReported && _throttleReported && _brakeReported);
    }

    private Gear Lever(InputConfig c)
    {
        for (int g = 0; g < c.ForwardGearButtons.Length; g++)
        {
            if (Button(c.ForwardGearButtons[g])) return (Gear)(g + 1);
        }
        return Button(c.ReverseGearButton) ? Gear.Reverse : Gear.Neutral;
    }

    private short Axis(PedalConfig pc, ref bool reported)
    {
        short raw = SDL_GetJoystickAxis(_joystick, pc.Axis);
        if (!reported && raw != 0) reported = true;
        return reported ? raw : pc.Inverted ? short.MaxValue : short.MinValue;
    }

    private bool Button(int index) => SDL_GetJoystickButton(_joystick, index);

    private void TryOpen()
    {
        using var ids = SDL_GetJoysticks();
        if (ids is null) return;
        for (int i = 0; i < ids.Count; i++)
        {
            string? name = SDL_GetJoystickNameForID(ids[i]);
            if (name is null || !name.Contains(Config.DeviceNameContains, StringComparison.OrdinalIgnoreCase))
                continue;
            _joystick = SDL_OpenJoystick(ids[i]);
            if (_joystick == null) continue;
            DeviceName = name;
            _clutch.Reset();
            _throttle.Reset();
            _brake.Reset();
            _clutchReported = _throttleReported = _brakeReported = false;
            return;
        }
    }

    private void Close()
    {
        if (_joystick != null) SDL_CloseJoystick(_joystick);
        _joystick = null;
        DeviceName = null;
    }

    public void Dispose()
    {
        Close();
        SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_JOYSTICK);
    }
}
