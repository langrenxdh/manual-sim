using Sim.Core;
using static Sim.Training.Tests.Attempt;

namespace Sim.Training.Tests;

/// <summary>
/// Scoring acceptance tests S1-S5 (docs/design.en.md, Practice and scoring): scripted driving that
/// I would call good or bad must score that way. Tune config/exercises.json, never these intents.
/// </summary>
public class ExerciseTests
{
    /// <summary>Clutch down in 1st from the start, released linearly from t = 0.5 s.</summary>
    private static DriverInput FlatPullAway(double t, double releaseS, double throttle) =>
        new(Ramp(t, 0.5, releaseS, 1, 0), Throttle(t, 0.5, throttle), 0, Gear.First);

    /// <summary>
    /// Proper hill technique: off the brake at 1 s, throttle in, clutch up to the bite over 0.6 s,
    /// hold it 0.5 s, then release over <paramref name="finalReleaseS"/>.
    /// </summary>
    private static DriverInput HillStart(double t, double throttle, double finalReleaseS, bool brakeFirst, double handbrakeOffS)
    {
        double clutch = t < 1.0 ? 1 : t < 1.6 ? Ramp(t, 1.0, 0.6, 1, 0.55) : t < 2.1 ? 0.55 : Ramp(t, 2.1, finalReleaseS, 0.55, 0);
        double brake = brakeFirst && t < 1.0 ? 0.6 : 0;
        return new DriverInput(clutch, Throttle(t, 1.0, throttle), brake, Gear.First, Handbrake: t < handbrakeOffS);
    }

    [Fact]
    public void S1_SmoothFlatPullAway_CompletesWithAtLeastB()
    {
        var s = Run("flatPullAway", t => FlatPullAway(t, releaseS: 2, throttle: 0));

        Assert.Equal(AttemptPhase.Completed, s.Phase);
        Assert.Equal(0, s.Metrics.Stalls);
        Assert.True(s.Result!.Score >= 75, $"score {s.Result.Score:F1}");
    }

    [Fact]
    public void S2_ClutchDump_FailsWithAStallAndZero()
    {
        var s = Run("flatPullAway", t => FlatPullAway(t, releaseS: 0.2, throttle: 0));

        Assert.Equal(AttemptPhase.Failed, s.Phase);
        Assert.Equal("stalled", s.FailReason);
        Assert.Equal(0, s.Result!.Score);
    }

    [Fact]
    public void S3_HillStartWithAssist_GoodTechnique_CompletesWithoutRollbackWithAtLeastB()
    {
        var s = Run("hillStartAssist", t => HillStart(t, throttle: 0.3, finalReleaseS: 3, brakeFirst: true, handbrakeOffS: 0));

        Assert.Equal(AttemptPhase.Completed, s.Phase);
        Assert.True(s.Metrics.RollbackM < 0.1, $"rollback {s.Metrics.RollbackM:F2} m");
        Assert.True(s.Result!.Score >= 75, $"score {s.Result.Score:F1}");
    }

    [Fact]
    public void S4_HandbrakeReleasedWithoutThrottleOrClutch_RollsBackAndFails()
    {
        var s = Run("hillStartHandbrake", t => new DriverInput(1, 0, 0, Gear.First, Handbrake: t < 0.5));

        Assert.Equal(AttemptPhase.Failed, s.Phase);
        Assert.True(s.Metrics.RollbackM > 0.5);
        Assert.Equal(0, s.Result!.Score);
    }

    [Fact]
    public void S4b_HillStartWithHandbrake_GoodTechnique_Completes()
    {
        var s = Run("hillStartHandbrake", t => HillStart(t, throttle: 0.35, finalReleaseS: 3, brakeFirst: false, handbrakeOffS: 2.1));

        Assert.Equal(AttemptPhase.Completed, s.Phase);
        Assert.True(s.Result!.Score >= 75, $"score {s.Result.Score:F1}");
    }

    [Fact]
    public void S5a_FlaredPullAway_ScoresLowerThanSmooth_AndNamesClutchOrJerk()
    {
        var smooth = Run("flatPullAway", t => FlatPullAway(t, releaseS: 2, throttle: 0)).Result!;
        var flared = Run("flatPullAway", t => FlatPullAway(t, releaseS: 3, throttle: 0.8)).Result!;

        Assert.False(flared.Failed);
        Assert.True(flared.Score < smooth.Score - 25, $"flared {flared.Score:F1} vs smooth {smooth.Score:F1}");
        Assert.Contains(flared.Biggest!.Metric, new[] { Metric.ClutchSlipEnergyKJ, Metric.PeakJerkMps3 });
        Assert.True(flared.Metrics.OverRevS > 0, "an 80 % throttle flare should reach the red zone");
    }

    [Fact]
    public void S5b_GrindingIntoSecond_ScoresLowerThanCleanShifts()
    {
        var clean = Run("smoothUpshifts", t => Upshifts(t, grind: false)).Result!;
        var grind = Run("smoothUpshifts", t => Upshifts(t, grind: true)).Result!;

        Assert.False(clean.Failed);
        Assert.False(grind.Failed);
        Assert.Equal(0, clean.Metrics.GrindingS);
        Assert.True(grind.Metrics.GrindingS > 0);
        Assert.True(grind.Score < clean.Score, $"grind {grind.Score:F1} vs clean {clean.Score:F1}");
    }

    /// <summary>1st from rest, then 2nd at 4.5 s and 3rd at 8.5 s with the clutch released over 0.5 s.
    /// With <paramref name="grind"/> the lever goes into 2nd before the clutch is down.</summary>
    private static DriverInput Upshifts(double t, bool grind)
    {
        Gear lever = t < (grind ? 4.2 : 4.5) ? Gear.First
            : t < 4.9 && !grind ? Gear.Neutral
            : t < 8.5 ? Gear.Second
            : t < 8.9 ? Gear.Neutral
            : Gear.Third;
        double clutch = t < 0.5 ? 1
            : t < 2.5 ? Ramp(t, 0.5, 1.5, 1, 0)
            : t < 4.3 ? Ramp(t, 4.1, 0.2, 0, 1)
            : t < 5.0 ? 1
            : t < 8.3 ? Ramp(t, 5.0, 0.5, 1, 0)
            : t < 9.0 ? Ramp(t, 8.1, 0.2, 0, 1)
            : Ramp(t, 9.0, 0.5, 1, 0);
        double throttle = t < 4.1 ? Throttle(t, 0.5, 0.5)
            : t < 5.0 ? Ramp(t, 4.0, 0.2, 0.5, 0)
            : t < 8.1 ? Throttle(t, 5.0, 0.5)
            : t < 9.0 ? Ramp(t, 8.0, 0.2, 0.5, 0)
            : Throttle(t, 9.0, 0.5);
        return new DriverInput(clutch, throttle, 0, lever);
    }
}
