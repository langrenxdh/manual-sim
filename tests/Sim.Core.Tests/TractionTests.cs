namespace Sim.Core.Tests;

/// <summary>Acceptance tests T18-T20 for longitudinal traction (M12, docs/design.en.md).</summary>
public class TractionTests
{
    private static VehicleParams Car(string file) =>
        VehicleParams.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", file)));

    private static readonly VehicleParams Golf_ = Golf.Load();

    /// <summary>T18: ordinary driving in the Golf never makes the tyres slip.</summary>
    [Fact]
    public void T18_OrdinaryDriving_NeverSlips()
    {
        // T1-style pull-away, then a brisk part-throttle run up through 2nd, then a firm stop.
        var sim = new Simulator(Golf_, Road.Flat(), Gear.First);
        var log = Script.Run(sim, 20, t =>
        {
            if (t < 6) return new DriverInput(Script.Ramp(t, 0.5, 2.5, 1, 0), Script.Ramp(t, 3, 1, 0, 0.4), 0, Gear.First);
            if (t < 6.6) return new DriverInput(1, 0, 0, t < 6.3 ? Gear.Neutral : Gear.Second);
            if (t < 14) return new DriverInput(Script.Ramp(t, 6.6, 0.8, 1, 0), 0.5, 0, Gear.Second);
            return new DriverInput(1, 0, 0.6, Gear.Neutral);
        });

        Assert.All(log, s => Assert.False(s.WheelSlip, $"tyres slipped at {s.TimeS:F2} s"));
        Assert.All(log, s => Assert.Equal(s.SpeedMps, s.DrivenWheelSpeedMps, 9));
        Assert.True(log.Max(s => s.SpeedKmh) > 30, "the run should get going");
    }

    /// <summary>
    /// T19: a Mustang launched from 4000 rpm with the clutch dropped spins its rear tyres; while they slide
    /// they pass slidingMu x the rear axle's load (weight transfer included) to the road, and they grip
    /// again soon after the throttle is lifted.
    /// </summary>
    [Fact]
    public void T19_ClutchDropAt4000_SpinsTheRears_AtTheSlidingLimit_ThenGripsWhenLifted()
    {
        var p = Car("mustang-gt.json");
        Assert.True(p.Traction.RearWheelDrive);
        var sim = new Simulator(p, Road.Flat(), Gear.First, engineRpm: 4000);
        var log = Script.Run(sim, 3.5, t => new DriverInput(Script.Ramp(t, 0.1, 0.1, 1, 0), t < 2 ? 1 : 0, 0, Gear.First));

        Assert.Contains(log, s => s.WheelSlip && s.DrivenWheelSpeedMps > s.SpeedMps + 1);

        // Steady sliding (weight transfer settled): m a = slidingMu (N_static + m a h / L) - rolling - aero.
        var t_ = p.Traction;
        var st = p.Steering;
        double m = p.Chassis.MassKg, g = p.Environment.GravityMps2;
        double staticRear = m * g * st.CgToFrontAxleM / st.WheelbaseM;
        var sliding = log.Between(1.0, 1.9).Where(s => s.WheelSlip).ToList();
        Assert.True(sliding.Count > 500, $"only {sliding.Count} ms of sliding between 1.0 and 1.9 s");
        foreach (var s in sliding.Where((_, i) => i % 50 == 0))
        {
            double aero = 0.5 * p.Environment.AirDensityKgM3 * p.Chassis.DragAreaM2 * s.SpeedMps * s.SpeedMps;
            double rolling = p.Chassis.RollingResistanceCoeff * m * g;
            double expected = (t_.SlidingMu * staticRear - rolling - aero) / (m - t_.SlidingMu * m * t_.CgHeightM / st.WheelbaseM);
            Assert.InRange(s.AccelerationMps2, expected * 0.98, expected * 1.02);
        }

        Assert.True(log.Between(2.5, 3.5).All(s => !s.WheelSlip), "the rears should grip again once the throttle is lifted");
    }

    /// <summary>
    /// T20: a Mustang at 60 km/h in 4th, shifted to 1st with the clutch let out fast, locks its rear tyres
    /// (they turn slower than the road) and then recovers; a front-wheel-drive Type R dropped from 5000 rpm
    /// spins its fronts.
    /// </summary>
    [Fact]
    public void T20_FastDownshiftLocksTheRears_AndAFrontDriverSpinsItsFronts()
    {
        var mustang = Car("mustang-gt.json");
        var down = Script.Run(new Simulator(mustang, Road.Flat(), Gear.Fourth, 60 / 3.6), 3, t => new DriverInput(
            t < 0.6 ? Script.Ramp(t, 0, 0.1, 0, 1) : Script.Ramp(t, 0.6, 0.15, 1, 0), 0, 0,
            t < 0.2 ? Gear.Fourth : t < 0.4 ? Gear.Neutral : Gear.First));

        Assert.Contains(down, s => s.WheelSlip && s.DrivenWheelSpeedMps < s.SpeedMps - 1);
        Assert.False(down[^1].WheelSlip);
        Assert.Equal(down[^1].SpeedMps, down[^1].DrivenWheelSpeedMps, 9);

        var typeR = Car("civic-type-r.json");
        Assert.False(typeR.Traction.RearWheelDrive);
        var launch = Script.Run(new Simulator(typeR, Road.Flat(), Gear.First, engineRpm: 5000), 2,
            t => new DriverInput(Script.Ramp(t, 0.1, 0.1, 1, 0), 1, 0, Gear.First));
        Assert.Contains(launch, s => s.WheelSlip && s.DrivenWheelSpeedMps > s.SpeedMps + 1);
    }
}
