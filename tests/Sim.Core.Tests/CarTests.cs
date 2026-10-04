namespace Sim.Core.Tests;

/// <summary>
/// T11 (M7): every shipped car is drivable: its config is valid, it idles steadily at its own idle
/// speed, and a gentle T1-style pull-away in 1st with no throttle does not stall.
/// </summary>
public class CarTests
{
    public static TheoryData<string> Cars =>
    [
        "golf-110tsi.json", "small-na-1.5.json", "diesel-2.0-turbo.json",
        "mustang-gt.json", "civic-type-r.json", "gr86.json", "mx5.json",
    ];

    private static VehicleParams Load(string file) =>
        VehicleParams.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", file)));

    [Theory]
    [MemberData(nameof(Cars))]
    public void T11_EveryCar_IdlesSteadily(string file)
    {
        var p = Load(file);
        var log = Script.Run(new Simulator(p, Road.Flat()), 10, _ => new DriverInput(0, 0, 0, Gear.Neutral));

        Assert.All(log.Between(5, 10), s => Assert.InRange(s.EngineRpm, p.Engine.IdleRpm - 50, p.Engine.IdleRpm + 50));
    }

    [Theory]
    [MemberData(nameof(Cars))]
    public void T11_EveryCar_PullsAwayGentlyWithoutStalling(string file)
    {
        var p = Load(file);
        var log = Script.Run(new Simulator(p, Road.Flat()), 8,
            t => new DriverInput(Script.Ramp(t, 0.5, 2.5, 1, 0), 0, 0, Gear.First));

        Assert.All(log, s => Assert.True(s.EngineRpm > p.Engine.StallRpm, $"stalled at {s.TimeS:F2} s"));
        Assert.True(log[^1].SpeedKmh > 3, $"creeping at {log[^1].SpeedKmh:F1} km/h");
    }

    [Fact]
    public void T11_CarsFeelDifferent_PeakNetTorqueAndRpmDiffer()
    {
        double PeakNetAt(VehicleParams p, double rpm) =>
            p.Engine.NaTorqueNm.Evaluate(rpm) + p.Engine.BoostTorqueNm.Evaluate(rpm) - p.Engine.FrictionTorqueNm.Evaluate(rpm);

        var golf = Load("golf-110tsi.json");
        var na = Load("small-na-1.5.json");
        var diesel = Load("diesel-2.0-turbo.json");

        Assert.True(PeakNetAt(diesel, 2000) > PeakNetAt(golf, 2000) + 50, "the diesel pulls much harder at 2000 rpm");
        Assert.True(PeakNetAt(na, 1500) < PeakNetAt(golf, 1500) - 80, "the small NA engine is weak low down");
        Assert.True(diesel.Engine.RedlineRpm < golf.Engine.RedlineRpm && na.Engine.RedlineRpm > golf.Engine.RedlineRpm);
    }
}
