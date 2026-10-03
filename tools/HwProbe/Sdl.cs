using SDL;
using static SDL.SDL3;

namespace HwProbe;

/// <summary>SDL3 lifetime plus small helpers shared by the commands. Joystick + haptic only, never video.</summary>
internal sealed unsafe class Sdl : IDisposable
{
    public const ushort LogitechVendorId = 0x046D;

    public static Sdl? Init(Options opts)
    {
        // A console app has no focused window; without this SDL may drop input from the wheel.
        SDL_SetHint(SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");
        if (opts.Lg4ff is bool lg4ff)
            SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_LG4FF, lg4ff ? "1" : "0");

        if (!SDL_Init(SDL_InitFlags.SDL_INIT_JOYSTICK | SDL_InitFlags.SDL_INIT_HAPTIC))
        {
            Console.Error.WriteLine($"SDL_Init failed: {SDL_GetError()}");
            return null;
        }
        int v = SDL_GetVersion();
        string lg4ffState = opts.Lg4ff switch { true => "forced on", false => "forced off", null => "SDL default (normally on)" };
        Console.WriteLine($"SDL {v / 1000000}.{v / 1000 % 1000}.{v % 1000} ({SDL_GetRevision()}), " +
                          $"HIDAPI LG4FF driver: {lg4ffState}");
        return new Sdl();
    }

    public void Dispose() => SDL_Quit();

    public static SDL_JoystickID[] JoystickIds()
    {
        using var ids = SDL_GetJoysticks();
        if (ids is null) return [];
        var result = new SDL_JoystickID[ids.Count];
        for (int i = 0; i < ids.Count; i++) result[i] = ids[i];
        return result;
    }

    /// <summary>Opens the requested joystick, or the first Logitech one, or the first one. Null if none.</summary>
    public static SDL_Joystick* OpenJoystick(int? index)
    {
        var ids = JoystickIds();
        if (ids.Length == 0)
        {
            Console.Error.WriteLine("No joystick found. Is the G29 plugged in (and its mode switch set for PC)?");
            return null;
        }

        int pick;
        if (index is int n)
        {
            if (n < 0 || n >= ids.Length)
            {
                Console.Error.WriteLine($"--device {n} out of range (found {ids.Length}). Run `HwProbe list`.");
                return null;
            }
            pick = n;
        }
        else
        {
            pick = Array.FindIndex(ids, id => SDL_GetJoystickVendorForID(id) == LogitechVendorId);
            if (pick < 0) pick = 0;
        }

        var joy = SDL_OpenJoystick(ids[pick]);
        if (joy == null)
        {
            Console.Error.WriteLine($"SDL_OpenJoystick failed: {SDL_GetError()}");
            return null;
        }
        Console.WriteLine($"Using [{pick}] {SDL_GetJoystickName(joy)} " +
                          $"(VID {SDL_GetJoystickVendor(joy):X4} PID {SDL_GetJoystickProduct(joy):X4})");
        return joy;
    }

    public static string Guid(SDL_Joystick* joy)
    {
        var guid = SDL_GetJoystickGUID(joy);
        byte* buf = stackalloc byte[33];
        SDL_GUIDToString(guid, buf, 33);
        return new string((sbyte*)buf);
    }

    public static string FeatureNames(uint features)
    {
        (uint bit, string name)[] all =
        [
            (SDL_HAPTIC_CONSTANT, "constant"), (SDL_HAPTIC_SINE, "sine"), (SDL_HAPTIC_SQUARE, "square"),
            (SDL_HAPTIC_TRIANGLE, "triangle"), (SDL_HAPTIC_SAWTOOTHUP, "saw-up"), (SDL_HAPTIC_SAWTOOTHDOWN, "saw-down"),
            (SDL_HAPTIC_RAMP, "ramp"), (SDL_HAPTIC_SPRING, "spring"), (SDL_HAPTIC_DAMPER, "damper"),
            (SDL_HAPTIC_INERTIA, "inertia"), (SDL_HAPTIC_FRICTION, "friction"), (SDL_HAPTIC_LEFTRIGHT, "left-right"),
            (SDL_HAPTIC_CUSTOM, "custom"), (SDL_HAPTIC_GAIN, "gain"), (SDL_HAPTIC_AUTOCENTER, "autocenter"),
            (SDL_HAPTIC_STATUS, "status"), (SDL_HAPTIC_PAUSE, "pause"),
        ];
        var names = all.Where(f => (features & f.bit) != 0).Select(f => f.name).ToList();
        return names.Count == 0 ? "(none)" : string.Join(", ", names);
    }
}
