using SDL;
using static SDL.SDL3;

namespace HwProbe;

/// <summary>`HwProbe list`: every joystick SDL sees, its layout and its haptic capabilities.</summary>
internal static unsafe class ListCommand
{
    public static int Run()
    {
        var ids = Sdl.JoystickIds();
        if (ids.Length == 0)
        {
            Console.WriteLine("No joystick found. Is the G29 plugged in (and its mode switch set for PC)?");
            return 1;
        }

        for (int i = 0; i < ids.Length; i++)
        {
            var joy = SDL_OpenJoystick(ids[i]);
            if (joy == null)
            {
                Console.WriteLine($"[{i}] {SDL_GetJoystickNameForID(ids[i])}: open failed: {SDL_GetError()}");
                continue;
            }

            Console.WriteLine($"[{i}] {SDL_GetJoystickName(joy)}");
            Console.WriteLine($"    VID {SDL_GetJoystickVendor(joy):X4}  PID {SDL_GetJoystickProduct(joy):X4}  " +
                              $"type {SDL_GetJoystickType(joy)}");
            Console.WriteLine($"    GUID {Sdl.Guid(joy)}");
            Console.WriteLine($"    path {SDL_GetJoystickPath(joy)}");
            Console.WriteLine($"    axes {SDL_GetNumJoystickAxes(joy)}  buttons {SDL_GetNumJoystickButtons(joy)}  " +
                              $"hats {SDL_GetNumJoystickHats(joy)}");
            PrintHaptic(joy);
            SDL_CloseJoystick(joy);
        }
        return 0;
    }

    private static void PrintHaptic(SDL_Joystick* joy)
    {
        if (!SDL_IsJoystickHaptic(joy))
        {
            Console.WriteLine($"    haptic: no ({SDL_GetError()})");
            return;
        }
        var haptic = SDL_OpenHapticFromJoystick(joy);
        if (haptic == null)
        {
            Console.WriteLine($"    haptic: reported, but open failed: {SDL_GetError()}");
            return;
        }
        Console.WriteLine($"    haptic: yes, {SDL_GetNumHapticAxes(haptic)} axes, " +
                          $"max effects {SDL_GetMaxHapticEffects(haptic)} " +
                          $"({SDL_GetMaxHapticEffectsPlaying(haptic)} playing at once)");
        Console.WriteLine($"    effects: {Sdl.FeatureNames(SDL_GetHapticFeatures(haptic))}");
        Console.WriteLine($"    rumble: {(SDL_HapticRumbleSupported(haptic) ? "yes" : "no")}");
        SDL_CloseHaptic(haptic);
    }
}
