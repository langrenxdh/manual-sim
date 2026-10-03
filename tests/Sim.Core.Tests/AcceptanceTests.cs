namespace Sim.Core.Tests;

/// <summary>
/// Behaviour acceptance tests T1–T5 from docs/design.md (行为验收测试). These encode how the real car
/// behaves; never weaken them to make the model pass — fix the model instead.
/// </summary>
public class AcceptanceTests
{
    private static readonly VehicleParams P = Golf.Load();

    /// <summary>T1: flat road, 1st gear, no throttle, clutch released linearly over 2 s.</summary>
    [Fact]
    public void T1_SlowClutchReleaseInFirst_DoesNotStall_SettlesAtIdleCreep()
    {
        var sim = new Simulator(P, Road.Flat(), Gear.First);
        var log = Script.Run(sim, 10, t => new DriverInput(
            Clutch: Script.Ramp(t, 0.5, 2.0, 1, 0), Throttle: 0, Brake: 0, Shifter: Gear.First));

        double minRpm = log.Min(s => s.EngineRpm);
        Assert.True(minRpm > P.Engine.StallRpm, $"engine dropped to {minRpm:F0} rpm (stall {P.Engine.StallRpm})");

        foreach (var s in log.Between(8, 10))
        {
            Assert.InRange(s.EngineRpm, P.Engine.IdleRpm - 50, P.Engine.IdleRpm + 50);
            Assert.True(s.ClutchLocked, $"clutch slipping at t={s.TimeS:F3}");
            Assert.InRange(s.SpeedKmh, 6, 7);
        }
    }

    /// <summary>T2: as T1 but the clutch is dumped in 0.2 s.</summary>
    [Fact]
    public void T2_ClutchDumpInFirst_Stalls()
    {
        var sim = new Simulator(P, Road.Flat(), Gear.First);
        var log = Script.Run(sim, 4, t => new DriverInput(
            Clutch: Script.Ramp(t, 0.5, 0.2, 1, 0), Throttle: 0, Brake: 0, Shifter: Gear.First));

        var fellBelowStall = log.FirstOrDefault(s => s.EngineRpm < P.Engine.StallRpm);
        Assert.True(fellBelowStall.TimeS > 0 && fellBelowStall.TimeS <= 2.7,
            "engine should fall below stall speed within 2 s of the clutch dump");
        var last = log[^1];
        Assert.False(last.Firing);
        Assert.True(last.EngineRpm < 1, $"engine should be stopped, still at {last.EngineRpm:F0} rpm");
    }

    /// <summary>T3: stationary on a 10 % grade, brake held then released, no other input.</summary>
    [Fact]
    public void T3_HillHoldHoldsForHoldTime_ThenReleasesAndCarRollsBack()
    {
        const double grade = 0.10;
        const double brakeRelease = 1.0;
        double holdTime = P.HillHold.HoldTimeS;
        var sim = new Simulator(P, Road.Constant(grade), Gear.First);
        var log = Script.Run(sim, brakeRelease + holdTime + 2.5, t => new DriverInput(
            Clutch: 1, Throttle: 0, Brake: t < brakeRelease ? 1 : 0, Shifter: Gear.First));

        // Held still for the hold time after the brake is released.
        foreach (var s in log.Between(0, brakeRelease + holdTime - 0.05))
            Assert.True(Math.Abs(s.SpeedMps) < 0.01, $"car moving at t={s.TimeS:F3}: {s.SpeedMps:F3} m/s");

        // Hold duration matches the parameter.
        double holdStart = log.First(s => s.HillHold == HillHoldState.Holding).TimeS;
        double holdEnd = log.First(s => s.HillHold == HillHoldState.ReleasingRollback).TimeS;
        Assert.InRange(holdEnd - holdStart, holdTime - 0.1, holdTime + 0.1);

        // Release is a ramp, not a step.
        var release = log.Where(s => s.TimeS >= holdEnd).Select(s => s.HillHoldForceN).ToList();
        for (int i = 1; i < release.Count; i++) Assert.True(release[i] <= release[i - 1]);
        double maxDrop = release.Zip(release.Skip(1), (a, b) => a - b).Max();
        Assert.True(maxDrop <= P.HillHold.ReleaseRateNPerS * Simulator.StepS + 1e-9);

        // Car starts rolling back soon after release.
        var rollStart = log.First(s => s.TimeS > brakeRelease && s.SpeedMps < -0.01);
        Assert.InRange(rollStart.TimeS - brakeRelease, holdTime, holdTime + 0.5);
        Assert.True(log[^1].SpeedMps < -0.1, $"final speed {log[^1].SpeedMps:F2} m/s, expected rolling back");
    }

    /// <summary>T4: 60 km/h in 4th, clutch in, select 1st, release the clutch over 0.5 s.</summary>
    [Fact]
    public void T4_DownshiftToFirstAt60_DragsEngineIntoRedZone_AndBrakesCar()
    {
        var sim = new Simulator(P, Road.Flat(), Gear.Fourth, Units.KmhToMps(60));
        var log = Script.Run(sim, 3, t => new DriverInput(
            Clutch: t < 0.8 ? Script.Ramp(t, 0, 0.2, 0, 1) : Script.Ramp(t, 0.8, 0.5, 1, 0),
            Throttle: 0, Brake: 0,
            Shifter: t < 0.4 ? Gear.Fourth : t < 0.6 ? Gear.Neutral : Gear.First));

        Assert.Equal(Gear.First, log[^1].EngagedGear);
        double peakRpm = log.Between(0.8, 3).Max(s => s.EngineRpm);
        Assert.True(peakRpm >= P.Engine.RedlineRpm, $"peak {peakRpm:F0} rpm, red zone starts at {P.Engine.RedlineRpm}");

        // Strongest deceleration averaged over 100 ms, so a single-step impulse does not count.
        const int window = 100;
        double peakDecel = 0;
        var during = log.Between(0.8, 3).ToList();
        for (int i = 0; i + window < during.Count; i++)
            peakDecel = Math.Max(peakDecel, (during[i].SpeedMps - during[i + window].SpeedMps) / (window * Simulator.StepS));
        double g = P.Environment.GravityMps2;
        Assert.True(peakDecel >= 0.3 * g, $"peak deceleration {peakDecel / g:F2} g, expected >= 0.3 g");
    }

    /// <summary>T5: lever pushed into gear without pressing the clutch.</summary>
    [Fact]
    public void T5_ShiftWithoutClutch_StaysInNeutralAndGrinds_UntilClutchPressed()
    {
        var sim = new Simulator(P, Road.Flat(), Gear.Neutral);
        var log = Script.Run(sim, 3, t => new DriverInput(
            Clutch: t < 1.5 ? 0 : 1, Throttle: 0, Brake: 0, Shifter: t < 0.5 ? Gear.Neutral : Gear.First));

        foreach (var s in log.Between(0.5, 1.5))
        {
            Assert.Equal(Gear.Neutral, s.EngagedGear);
            Assert.True(s.Grinding, $"expected grinding at t={s.TimeS:F3}");
        }
        foreach (var s in log.Between(1.6, 3))
        {
            Assert.Equal(Gear.First, s.EngagedGear);
            Assert.False(s.Grinding);
        }
    }
}
