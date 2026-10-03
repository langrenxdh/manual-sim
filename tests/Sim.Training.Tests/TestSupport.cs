using Sim.Core;

namespace Sim.Training.Tests;

internal static class Configs
{
    public static VehicleParams Golf() => VehicleParams.FromJson(Read("golf-110tsi.json"));

    public static ExerciseConfig Exercises() => ExerciseConfig.FromJson(Read("exercises.json"));

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", name));
}

/// <summary>Runs one scripted attempt the way the app does: simulator step, then session observe.</summary>
internal static class Attempt
{
    /// <summary>The scene's hill (config/scene.json) is a 10 % grade where hill starts begin.</summary>
    public const double HillGrade = 0.10;

    public static ExerciseSession Run(string exerciseId, Func<double, DriverInput> driver, double maxSeconds = 120)
    {
        var config = Configs.Exercises();
        var exercise = config.Find(exerciseId);
        var vehicle = ExerciseSession.ApplyTo(exercise, Configs.Golf());
        var road = exercise.Start == StartPoint.HillStart ? Road.Constant(HillGrade) : Road.Flat();
        var sim = new Simulator(vehicle, road);
        var session = new ExerciseSession(config, exercise, vehicle);
        int steps = (int)(maxSeconds / Simulator.StepS);
        for (int i = 0; i < steps && session.Phase == AttemptPhase.Running; i++)
        {
            var input = driver(i * Simulator.StepS);
            sim.Step(input);
            session.Observe(sim.State, input, Simulator.StepS);
        }
        return session;
    }

    /// <summary>Linear ramp from <paramref name="from"/> to <paramref name="to"/> over [start, start + duration].</summary>
    public static double Ramp(double t, double start, double duration, double from, double to)
    {
        if (t <= start) return from;
        if (t >= start + duration) return to;
        return from + (to - from) * (t - start) / duration;
    }

    /// <summary>A foot pressing the throttle: ramps in over 0.3 s rather than stepping.</summary>
    public static double Throttle(double t, double start, double level) => Ramp(t, start, 0.3, 0, level);
}
