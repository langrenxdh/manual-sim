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

    [Fact]
    public void Reverse_DrivesBackwards()
    {
        var sim = new Simulator(P, Road.Flat());
        var log = Script.Run(sim, 6, t => new DriverInput(t < 0.5 ? 1 : Script.Ramp(t, 0.5, 2.5, 1, 0), 0, 0, Gear.Reverse));
        Assert.Equal(Gear.Reverse, log[^1].EngagedGear);
        Assert.True(log[^1].SpeedMps < -1, $"speed {log[^1].SpeedMps:F2} m/s");
    }
}
