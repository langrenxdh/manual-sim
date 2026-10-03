namespace Sim.Core.Tests;

/// <summary>Sanity checks of individual parts of the model against hand calculations.</summary>
public class PhysicsTests
{
    private static readonly VehicleParams P = Golf.Load();

    private static DriverInput Idle(Gear gear, double clutch = 0) => new(clutch, 0, 0, gear);

    [Fact]
    public void EngineIdlesSteadilyInNeutral()
    {
        var sim = new Simulator(P, Road.Flat());
        var log = Script.Run(sim, 5, _ => Idle(Gear.Neutral));
        foreach (var s in log.Between(1, 5))
            Assert.InRange(s.EngineRpm, P.Engine.IdleRpm - 20, P.Engine.IdleRpm + 20);
    }

    [Fact]
    public void FirstGearAtIdle_GivesKinematicSpeed()
    {
        // v = ω_e · r / (i_1 · i_f): 800 rpm, 1.99 m tyre, 4.11 × 3.39 → 6.86 km/h.
        var g = P.Gearbox;
        double expected = Units.RpmToRadPerSec(P.Engine.IdleRpm) * P.Chassis.TyreCircumferenceM / (2 * Math.PI)
            / (g.ForwardRatios[0] * g.FinalDriveRatio);
        Assert.InRange(Units.MpsToKmh(expected), 6.8, 6.9);

        var sim = new Simulator(P, Road.Flat(), Gear.First, expected);
        var log = Script.Run(sim, 3, _ => Idle(Gear.First));
        Assert.All(log, s => Assert.True(s.ClutchLocked));
        Assert.InRange(log[^1].SpeedMps, expected * 0.99, expected * 1.01);
    }

    [Fact]
    public void LockedClutch_DoesNotChatter()
    {
        var sim = new Simulator(P, Road.Flat(), Gear.Third, Units.KmhToMps(50));
        var log = Script.Run(sim, 3, t => new DriverInput(0, 0.3, 0, Gear.Third));
        Assert.All(log, s => Assert.True(s.ClutchLocked, $"clutch unlocked at t={s.TimeS:F3}"));
    }

    [Fact]
    public void HoldingOnTenPercentGrade_NeedsAbout30NmAtTheCrank()
    {
        // docs/design.md: on a 10 % grade the engine must give about 30 Nm in 1st to stop the car rolling back.
        const double grade = 0.10;
        double theta = Math.Atan(grade);
        var g = P.Gearbox;
        double k = g.ForwardRatios[0] * g.FinalDriveRatio / (P.Chassis.TyreCircumferenceM / (2 * Math.PI));
        double mg = P.Chassis.MassKg * P.Environment.GravityMps2;
        double crank = mg * Math.Sin(theta) / (k * g.Efficiency);
        Assert.InRange(crank, 29, 34);

        // Parked in 1st with the engine off: compression holds the car, and the clutch carries the hill
        // torque minus what rolling resistance and gearbox drag take.
        double rolling = P.Chassis.RollingResistanceCoeff * mg * Math.Cos(theta) / (k * g.Efficiency);
        var sim = new Simulator(P, Road.Constant(grade), Gear.First, engineRpm: 0);
        var log = Script.Run(sim, 0.5, _ => Idle(Gear.First));
        Assert.All(log, s => Assert.Equal(0, s.SpeedMps));
        Assert.InRange(log[^1].ClutchTorqueNm, crank - rolling - g.InputShaftDragNm - 1e-6, crank);
    }

    [Fact]
    public void TopSpeedInSixth_IsPlausible()
    {
        // Golf 110TSI manual: about 200 km/h.
        var sim = new Simulator(P, Road.Flat(), Gear.Sixth, Units.KmhToMps(180));
        var log = Script.Run(sim, 120, _ => new DriverInput(0, 1, 0, Gear.Sixth));
        Assert.InRange(log[^1].SpeedKmh, 195, 220);
    }

    [Fact]
    public void Deterministic_SameInputSameResult()
    {
        static List<SimState> Run()
        {
            var sim = new Simulator(P, Road.Flat(), Gear.First);
            return Script.Run(sim, 4, t => new DriverInput(Script.Ramp(t, 0.2, 1.5, 1, 0), Script.Ramp(t, 0.5, 1, 0, 0.3), 0, Gear.First));
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void StalledEngine_RestartsWithStarter()
    {
        var sim = new Simulator(P, Road.Flat(), engineRpm: 0);
        var log = Script.Run(sim, 4, t => new DriverInput(1, 0, 0, Gear.Neutral, Starter: t < 1));
        Assert.InRange(log[^1].EngineRpm, P.Engine.IdleRpm - 50, P.Engine.IdleRpm + 50);
    }

    [Fact]
    public void StalledEngine_StaysStoppedWithoutStarter()
    {
        var sim = new Simulator(P, Road.Flat(), engineRpm: 0);
        var log = Script.Run(sim, 1, _ => Idle(Gear.Neutral));
        Assert.Equal(0, log[^1].EngineRpm);
    }

    [Fact]
    public void LuggingInHighGear_Shudders()
    {
        // 6th gear at 30 km/h (~550 rpm kinematic) with throttle: low rpm + high load.
        var sim = new Simulator(P, Road.Flat(), Gear.Fourth, Units.KmhToMps(25));
        var log = Script.Run(sim, 0.5, _ => new DriverInput(0, 0.8, 0, Gear.Fourth));
        Assert.True(log.Max(s => s.ShudderIntensity) > 0.3);

        var cruising = new Simulator(P, Road.Flat(), Gear.Third, Units.KmhToMps(60));
        var log2 = Script.Run(cruising, 0.5, _ => new DriverInput(0, 0.3, 0, Gear.Third));
        Assert.All(log2, s => Assert.Equal(0, s.ShudderIntensity));
    }

    [Fact]
    public void Brake_HoldsCarOnHillWithoutRollingBackOrForward()
    {
        var sim = new Simulator(P, Road.Constant(0.15), Gear.Neutral);
        var log = Script.Run(sim, 2, _ => new DriverInput(0, 0, 0.5, Gear.Neutral));
        Assert.All(log, s => Assert.Equal(0, s.SpeedMps));
    }
}
