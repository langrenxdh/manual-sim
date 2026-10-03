namespace Sim.Core.Tests;

internal static class Golf
{
    public static VehicleParams Load() =>
        VehicleParams.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", "golf-110tsi.json")));
}

/// <summary>Scripted pedal helpers. Time t is seconds since the start of the run.</summary>
internal static class Script
{
    /// <summary>Linear ramp from <paramref name="from"/> to <paramref name="to"/> over [start, start + duration].</summary>
    public static double Ramp(double t, double start, double duration, double from, double to)
    {
        if (t <= start) return from;
        if (t >= start + duration) return to;
        return from + (to - from) * (t - start) / duration;
    }

    /// <summary>Runs the simulator for <paramref name="seconds"/>, recording the state after every step.</summary>
    public static List<SimState> Run(Simulator sim, double seconds, Func<double, DriverInput> driver)
    {
        int steps = (int)Math.Round(seconds / Simulator.StepS);
        var log = new List<SimState>(steps);
        for (int i = 0; i < steps; i++)
        {
            double t = i * Simulator.StepS;
            sim.Step(driver(t));
            log.Add(sim.State);
        }
        return log;
    }

    public static IEnumerable<SimState> Between(this IEnumerable<SimState> log, double from, double to) =>
        log.Where(s => s.TimeS > from && s.TimeS <= to);
}
