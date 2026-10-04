namespace Sim.Core.Tests;

public class GearboxTests
{
    private static readonly VehicleParams P = Golf.Load();

    [Fact]
    public void LeverToNeutral_DisengagesImmediately_EvenWithClutchUp()
    {
        var sim = new Simulator(P, Road.Flat(), Gear.Third, Units.KmhToMps(40));
        sim.Step(new DriverInput(0, 0, 0, Gear.Third));
        Assert.Equal(Gear.Third, sim.State.EngagedGear);
        sim.Step(new DriverInput(0, 0, 0, Gear.Neutral));
        Assert.Equal(Gear.Neutral, sim.State.EngagedGear);
        Assert.False(sim.State.Grinding);
    }

    [Fact]
    public void LeverGearToGear_WithoutClutch_Grinds()
    {
        var sim = new Simulator(P, Road.Flat(), Gear.Second, Units.KmhToMps(30));
        sim.Step(new DriverInput(0, 0, 0, Gear.Third));
        Assert.Equal(Gear.Neutral, sim.State.EngagedGear);
        Assert.True(sim.State.Grinding);
    }

    /// <summary>M13: the grind's speed difference is the input shaft against the gear being forced in.</summary>
    [Fact]
    public void Grinding_ReportsTheSpeedDifferenceToTheForcedGear_AndZeroOnceItStops()
    {
        double v = Units.KmhToMps(30), r = P.Chassis.TyreCircumferenceM / (2 * Math.PI);
        double k2 = P.Gearbox.ForwardRatios[1] * P.Gearbox.FinalDriveRatio / r, k3 = P.Gearbox.ForwardRatios[2] * P.Gearbox.FinalDriveRatio / r;
        var sim = new Simulator(P, Road.Flat(), Gear.Second, v);
        sim.Step(new DriverInput(0, 0, 0, Gear.Third));
        Assert.InRange(sim.State.GrindSlipRadPerS, (k2 - k3) * v * 0.99, (k2 - k3) * v * 1.01);

        sim.Step(new DriverInput(0, 0, 0, Gear.Neutral));
        Assert.False(sim.State.Grinding);
        Assert.Equal(0, sim.State.GrindSlipRadPerS);
    }

    [Fact]
    public void Reverse_DrivesBackwards()
    {
        var sim = new Simulator(P, Road.Flat());
        var log = Script.Run(sim, 6, t => new DriverInput(t < 0.5 ? 1 : Script.Ramp(t, 0.5, 2.5, 1, 0), 0, 0, Gear.Reverse));
        Assert.Equal(Gear.Reverse, log[^1].EngagedGear);
        Assert.True(log[^1].SpeedMps < -1, $"speed {log[^1].SpeedMps:F2} m/s");
    }
}
