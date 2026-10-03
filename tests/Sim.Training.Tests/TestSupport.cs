using Sim.Core;

namespace Sim.Training.Tests;

internal static class Configs
{
    public static VehicleParams Golf() => VehicleParams.FromJson(Read("golf-110tsi.json"));

    public static ExerciseConfig Exercises() => ExerciseConfig.FromJson(Read("exercises.json"));

    public static Scene Scene() => Training.Scene.FromJson(Read("scene.json"));

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", name));
}

/// <summary>
/// Runs one scripted attempt the way the app does: on the real scene's road from the exercise's start
/// point, simulator step, then session observe.
/// </summary>
internal static class Attempt
{
    public static ExerciseSession Run(string exerciseId, Func<double, DriverInput> driver, double maxSeconds = 160) =>
        Run(exerciseId, (t, _) => driver(t), maxSeconds);

    /// <summary>A driver that reacts to the car (position, speed, ...), for closed-loop scripts.</summary>
    public static ExerciseSession Run(string exerciseId, Func<double, SimState, DriverInput> driver, double maxSeconds = 160)
    {
        var config = Configs.Exercises();
        var scene = Configs.Scene();
        var exercise = config.Find(exerciseId);
        var vehicle = ExerciseSession.ApplyTo(exercise, Configs.Golf());
        var sim = new Simulator(vehicle, scene.BuildRoad(), positionM: scene.PositionOf(exercise.Start));
        var session = new ExerciseSession(config, exercise, vehicle, scene);
        int steps = (int)(maxSeconds / Simulator.StepS);
        for (int i = 0; i < steps && session.Phase == AttemptPhase.Running; i++)
        {
            var input = driver(i * Simulator.StepS, sim.State);
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
