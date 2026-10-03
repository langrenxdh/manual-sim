namespace Sim.Core.Tests;

public class ParamsTests
{
    private static readonly VehicleParams P = Golf.Load();

    [Fact]
    public void GolfConfig_LoadsAndRoundTrips()
    {
        Assert.Equal(6, P.Gearbox.ForwardRatios.Length);
        var again = VehicleParams.FromJson(P.ToJson());
        Assert.Equal(P.ToJson(), again.ToJson());
    }

    [Fact]
    public void UnknownField_IsRejected()
    {
        string json = P.ToJson().Replace("\"idleRpm\"", "\"idleRmp\"");
        Assert.ThrowsAny<Exception>(() => VehicleParams.FromJson(json));
    }

    [Fact]
    public void InvalidValue_IsRejected()
    {
        var bad = P with { Engine = P.Engine with { IdleRpm = P.Engine.StallRpm - 1 } };
        Assert.Throws<ArgumentException>(() => VehicleParams.FromJson(bad.ToJson()));
    }

    /// <summary>Full-load brake torque = gross NA + boost - friction; published 250 Nm @ 1500–3500 rpm.</summary>
    [Theory]
    [InlineData(1500)]
    [InlineData(2500)]
    [InlineData(3500)]
    public void FullLoadTorque_MatchesPublishedPeak(double rpm)
    {
        var e = P.Engine;
        double brake = e.NaTorqueNm.Evaluate(rpm) + e.BoostTarget.Evaluate(rpm) * e.BoostTorqueNm.Evaluate(rpm)
            - e.FrictionTorqueNm.Evaluate(rpm);
        Assert.InRange(brake, 245, 255);
    }

    /// <summary>Published 110 kW @ 5000–6000 rpm.</summary>
    [Theory]
    [InlineData(5000)]
    [InlineData(6000)]
    public void FullLoadPower_MatchesPublishedPeak(double rpm)
    {
        var e = P.Engine;
        double brake = e.NaTorqueNm.Evaluate(rpm) + e.BoostTarget.Evaluate(rpm) * e.BoostTorqueNm.Evaluate(rpm)
            - e.FrictionTorqueNm.Evaluate(rpm);
        double kw = brake * Units.RpmToRadPerSec(rpm) / 1000;
        Assert.InRange(kw, 107, 113);
    }

    [Fact]
    public void Curve_InterpolatesAndClamps()
    {
        var c = new Curve([(0, 0), (10, 100)]);
        Assert.Equal(0, c.Evaluate(-5));
        Assert.Equal(50, c.Evaluate(5));
        Assert.Equal(100, c.Evaluate(50));
    }
}
