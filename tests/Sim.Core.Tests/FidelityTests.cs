namespace Sim.Core.Tests;

/// <summary>
/// Acceptance tests T6-T10 for the M7 fidelity effects (docs/design.en.md, M7). Like T1-T5 they
/// state driving experience; tune config/golf-110tsi.json, never these intents.
/// </summary>
public class FidelityTests
{
    private static readonly VehicleParams Golf_ = Golf.Load();

    /// <summary>
    /// T6: abusing the clutch (stationary on the brakes, 1st gear, half throttle, clutch half way) heats it
    /// until it smells and then fades: it transmits less torque for the same pedal.
    /// </summary>
    [Fact]
    public void T6_SlippingTheClutchHard_HeatsItUntilItSmellsAndFades()
    {
        var sim = new Simulator(Golf_, Road.Flat());
        var abuse = new DriverInput(Clutch: 0.5, Throttle: 0.5, Brake: 1, Gear.First, Handbrake: true);
        var log = Script.Run(sim, 90, t => t < 1 ? abuse with { Clutch = 1 } : abuse);

        var early = log.Between(4, 5).Average(s => Math.Abs(s.ClutchTorqueNm));
        var late = log.Between(89, 90).Average(s => Math.Abs(s.ClutchTorqueNm));
        var smellAt = log.FirstOrDefault(s => s.ClutchSmell).TimeS;

        Assert.All(log, s => Assert.True(Math.Abs(s.SpeedMps) < 0.05, "the brakes hold the car throughout"));
        Assert.InRange(smellAt, 5, 80);
        Assert.True(log[^1].ClutchFrictionFactor < 0.9, $"friction factor {log[^1].ClutchFrictionFactor:F2}");
        Assert.True(late < early * 0.9, $"transmitted torque {early:F0} -> {late:F0} Nm");
    }

    /// <summary>T7: ordinary pull-aways (T1 style) barely warm the clutch: no smell, no fade.</summary>
    [Fact]
    public void T7_NormalPullAways_DoNotOverheatTheClutch()
    {
        var sim = new Simulator(Golf_, Road.Flat());
        var log = new List<SimState>();
        for (int i = 0; i < 5; i++)
        {
            // Clutch down, 1st, 2 s release, creep 5 s, then clutch down and brake to a stop for 5 s.
            log.AddRange(Script.Run(sim, 15, t => t < 1 ? new DriverInput(1, 0, 0, Gear.First)
                : t < 8 ? new DriverInput(Script.Ramp(t, 1, 2, 1, 0), 0, 0, Gear.First)
                : new DriverInput(1, 0, 0.5, Gear.Neutral)));
        }

        double rise = log.Max(s => s.ClutchTempC) - Golf_.Environment.AmbientTempC;
        Assert.True(rise < 15, $"clutch temperature rose {rise:F1} K");
        Assert.All(log, s => Assert.False(s.ClutchSmell));
        Assert.All(log, s => Assert.Equal(1, s.ClutchFrictionFactor, 9));
    }

    /// <summary>T8: a cold engine idles higher, then warms up while it runs; a warm one idles at the normal speed.</summary>
    [Fact]
    public void T8_ColdEngine_IdlesHigher_AndWarmsUp()
    {
        var e = Golf_.EngineThermal;
        var cold = Script.Run(new Simulator(Golf_, Road.Flat(), engineTempC: e.ColdReferenceC), 120,
            _ => new DriverInput(0, 0, 0, Gear.Neutral));
        var warm = Script.Run(new Simulator(Golf_, Road.Flat()), 10, _ => new DriverInput(0, 0, 0, Gear.Neutral));

        double coldIdle = cold.Between(8, 10).Average(s => s.EngineRpm);
        double warmIdle = warm.Between(8, 10).Average(s => s.EngineRpm);
        Assert.InRange(coldIdle, Golf_.Engine.IdleRpm + e.ColdIdleExtraRpm - 60, Golf_.Engine.IdleRpm + e.ColdIdleExtraRpm + 10);
        Assert.InRange(warmIdle, Golf_.Engine.IdleRpm - 50, Golf_.Engine.IdleRpm + 50);
        Assert.True(cold[^1].EngineTempC > e.ColdReferenceC + 5, $"after 2 min at idle {cold[^1].EngineTempC:F1} C");
        Assert.All(warm, s => Assert.InRange(s.EngineTempC, e.WarmC - 1, e.WarmC + 2));
    }

    /// <summary>T9: when cold, the higher internal friction leaves the idle controller less in reserve.</summary>
    [Fact]
    public void T9_ColdEngine_UsesMoreOfTheIdleControllersReserve()
    {
        var neutral = new DriverInput(0, 0, 0, Gear.Neutral);
        var cold = Script.Run(new Simulator(Golf_, Road.Flat(), engineTempC: Golf_.EngineThermal.ColdReferenceC), 10, _ => neutral);
        var warm = Script.Run(new Simulator(Golf_, Road.Flat()), 10, _ => neutral);

        Assert.True(cold.Between(8, 10).Average(s => s.IdleControlUsage) > warm.Between(8, 10).Average(s => s.IdleControlUsage) * 1.3);
    }

    /// <summary>T10: switching the air-con on loads the engine; the ECU raises idle a little to carry it.</summary>
    [Fact]
    public void T10_AirCon_LoadsTheIdleAndRaisesIt()
    {
        var off = Script.Run(new Simulator(Golf_, Road.Flat()), 10, _ => new DriverInput(0, 0, 0, Gear.Neutral));
        var on = Script.Run(new Simulator(Golf_, Road.Flat()), 10, _ => new DriverInput(0, 0, 0, Gear.Neutral, AirCon: true));

        double idleOn = on.Between(8, 10).Average(s => s.EngineRpm);
        Assert.InRange(idleOn, Golf_.Engine.IdleRpm + Golf_.AirCon.IdleBumpRpm - 30, Golf_.Engine.IdleRpm + Golf_.AirCon.IdleBumpRpm + 30);
        Assert.True(on.Between(8, 10).Average(s => s.IdleControlUsage) > off.Between(8, 10).Average(s => s.IdleControlUsage));
        Assert.True(on[^1].AirCon);
    }
}
