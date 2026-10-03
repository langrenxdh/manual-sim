namespace HwProbe;

internal sealed record Options(
    string Command,
    int? DeviceIndex,
    string? CsvPath,
    bool? Lg4ff,
    double StepSeconds,
    bool SweepOnly)
{
    public static Options? Parse(string[] args)
    {
        string command = "watch";
        int? device = null;
        string? csv = null;
        bool? lg4ff = null;
        double step = 2.0;
        bool sweepOnly = false;

        int i = 0;
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            command = args[0];
            i = 1;
        }
        if (command is not ("list" or "watch" or "ffb")) return null;

        for (; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--device" when i + 1 < args.Length && int.TryParse(args[i + 1], out int d):
                    device = d; i++; break;
                case "--csv" when i + 1 < args.Length:
                    csv = args[++i]; break;
                case "--lg4ff" when i + 1 < args.Length && args[i + 1] is "on" or "off":
                    lg4ff = args[++i] == "on"; break;
                case "--step" when i + 1 < args.Length && double.TryParse(args[i + 1], out double s) && s > 0:
                    step = s; i++; break;
                case "--sweep-only":
                    sweepOnly = true; break;
                default:
                    return null;
            }
        }
        return new Options(command, device, csv, lg4ff, step, sweepOnly);
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            HwProbe — M0 hardware probe for the G29 (SDL3 input + haptics)

            usage:
              HwProbe list                      list joysticks and their haptic features
              HwProbe watch [--csv FILE]        live raw axes / buttons / hats (default command)
              HwProbe ffb [--step SECONDS] [--sweep-only]
                                                sine sweep, then interactive force-feedback test

            common options:
              --device N        use joystick N from `list` (default: first Logitech device, else first)
              --lg4ff on|off    force SDL's HIDAPI Logitech wheel driver (LG4FF) on or off;
                                off = DirectInput. Default: SDL's default (normally on)
            """);
    }
}
