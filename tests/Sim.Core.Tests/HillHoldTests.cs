namespace Sim.Core.Tests;

public class HillHoldTests
{
    private static readonly VehicleParams P = Golf.Load();

    [Fact]
    public void Disabled_CarRollsBackAsSoonAsBrakeIsReleased()
    {
        var p = P with { HillHold = P.HillHold with { Enabled = false } };
        var sim = new Simulator(p, Road.Constant(0.10), Gear.First);
        var log = Script.Run(sim, 2, t => new DriverInput(1, 0, t < 1 ? 1 : 0, Gear.First));
        Assert.All(log, s => Assert.Equal(HillHoldState.Inactive, s.HillHold));
        Assert.True(log.First(s => s.SpeedMps < -0.01).TimeS < 1.1);
    }

    [Fact]
    public void FlatRoad_DoesNotArm()
    {
        var sim = new Simulator(P, Road.Flat(), Gear.First);
        var log = Script.Run(sim, 2, t => new DriverInput(1, 0, t < 1 ? 1 : 0, Gear.First));
        Assert.All(log, s => Assert.Equal(HillHoldState.Inactive, s.HillHold));
    }

    [Fact]
    public void PressingBrakeAgain_ReArmsAndRestartsTimer()
    {
        var sim = new Simulator(P, Road.Constant(0.10), Gear.First);
        var log = Script.Run(sim, 4, t => new DriverInput(1, 0, t < 0.5 || (t >= 1.5 && t < 2) ? 1 : 0, Gear.First));
        Assert.Equal(HillHoldState.Armed, log.Between(1.6, 1.9).First().HillHold);
        Assert.True(log.Between(0, 3.9).All(s => s.SpeedMps == 0), "car must stay held");
    }

    [Fact]
    public void DrivingAway_ReleasesEarly_WithoutRollingBack()
    {
        // 10 % grade, 1st gear: release brake, then throttle + clutch within the hold time.
        var sim = new Simulator(P, Road.Constant(0.10), Gear.First);
        var log = Script.Run(sim, 6, t => new DriverInput(
            Clutch: t < 1.2 ? 1 : Script.Ramp(t, 1.2, 2.5, 1, 0),
            Throttle: t < 1.0 ? 0 : 0.4,
            Brake: t < 0.5 ? 1 : 0,
            Shifter: Gear.First));
        Assert.Contains(log, s => s.HillHold == HillHoldState.ReleasingDriveAway);
        Assert.DoesNotContain(log, s => s.HillHold == HillHoldState.ReleasingRollback);
        Assert.All(log, s => Assert.True(s.SpeedMps > -0.01, $"rolled back at t={s.TimeS:F3}"));
        Assert.True(log[^1].SpeedMps > 1);
        Assert.True(log.Min(s => s.EngineRpm) > P.Engine.StallRpm);
    }
}
