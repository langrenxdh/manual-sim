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
}
