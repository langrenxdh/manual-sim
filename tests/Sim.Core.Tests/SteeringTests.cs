namespace Sim.Core.Tests;

/// <summary>Acceptance tests T12-T16 for steering (M9b, docs/design.en.md).</summary>
public class SteeringTests
{
    private static readonly VehicleParams Golf_ = Golf.Load();

    /// <summary>A car rolling in a gear at a speed, steering on, wheel held at an angle.</summary>
    private static List<SimState> Roll(Gear gear, double kmh, double wheelDeg, double seconds, double throttle = 0) =>
        Script.Run(new Simulator(Golf_, Road.Flat(), gear, kmh / 3.6, steering: true), seconds,
            _ => new DriverInput(0, throttle, 0, gear, SteeringWheelDeg: wheelDeg));

    /// <summary>T12: at walking pace the car follows the geometric (kinematic) turning circle.</summary>
    [Fact]
    public void T12_LowSpeed_TurnRadiusMatchesSteeringGeometry()
    {
        var st = Golf_.Steering;
        double wheelDeg = 180;
        var s = Roll(Gear.First, 7, wheelDeg, 2)[^1];

        double expected = st.WheelbaseM / Math.Tan(wheelDeg / st.SteeringRatio * Math.PI / 180);
        double radius = s.SpeedMps / s.YawRateRadPerS;
        Assert.False(s.FrontSliding);
        Assert.InRange(radius, expected * 0.98, expected * 1.02);
    }

    /// <summary>T13: too much lock for the speed saturates lateral grip (understeer) at about mu g.</summary>
    [Fact]
    public void T13_TooMuchLockForTheSpeed_UndersteersAtTheGripLimit()
    {
        var s = Roll(Gear.Third, 60, 200, 1, throttle: 0.3)[^1];

        double limit = Golf_.Steering.TyreGripMu * Golf_.Environment.GravityMps2;
        Assert.True(s.FrontSliding);
        Assert.Equal(limit, s.LateralAccelMps2, 6);
    }

    /// <summary>T14: wheel straight, car goes straight.</summary>
    [Fact]
    public void T14_WheelStraight_HeadingAndLineStayConstant()
    {
        var log = Roll(Gear.Second, 20, 0, 10, throttle: 0.2);

        Assert.All(log, s => Assert.Equal(0, s.HeadingRad, 12));
        Assert.All(log, s => Assert.Equal(0, s.WorldY, 12));
        Assert.True(log[^1].WorldX > 30);
    }

    /// <summary>
    /// T15: the wheel pulls back towards straight, harder as cornering builds, and goes light once the
    /// front tyres slide.
    /// </summary>
    [Fact]
    public void T15_AligningTorque_OpposesTheTurn_GrowsWithCornering_AndGoesLightWhenSliding()
    {
        var gentle = Roll(Gear.Third, 50, 20, 1, 0.3)[^1];
        var firmer = Roll(Gear.Third, 50, 45, 1, 0.3)[^1];
        var sliding = Roll(Gear.Third, 50, 200, 1, 0.3)[^1];
        var right = Roll(Gear.Third, 50, -45, 1, 0.3)[^1];

        Assert.False(firmer.FrontSliding);
        Assert.True(sliding.FrontSliding);
        Assert.True(gentle.AligningTorqueNm < 0 && firmer.AligningTorqueNm < gentle.AligningTorqueNm, "left turns pull the wheel right, harder when turning more");
        Assert.True(right.AligningTorqueNm > 0, "right turns pull the wheel left");
        Assert.True(Math.Abs(sliding.AligningTorqueNm) < Math.Abs(firmer.AligningTorqueNm), "past the limit the wheel goes light");
    }

    /// <summary>
    /// T17: at parking and roundabout speeds, where the tyre force is small, steering geometry still pulls
    /// the wheel back to straight; at a standstill nothing moves it.
    /// </summary>
    [Fact]
    public void T17_LowSpeed_WheelReturnsTowardsCentre_StandstillHoldsIt()
    {
        var left = Roll(Gear.First, 7, 180, 1)[^1];
        var right = Roll(Gear.First, 7, -180, 1)[^1];
        var parked = Script.Run(new Simulator(Golf_, Road.Flat(), Gear.Neutral, steering: true), 1,
            _ => new DriverInput(0, 0, 1, Gear.Neutral, SteeringWheelDeg: 180))[^1];

        Assert.True(left.AligningTorqueNm <= -0.3, $"left lock at 7 km/h: {left.AligningTorqueNm:F2} Nm towards centre");
        Assert.Equal(-left.AligningTorqueNm, right.AligningTorqueNm, 6);
        Assert.Equal(0, parked.AligningTorqueNm, 12);
    }

    /// <summary>T16: with steering off (the hill road and every older test) the car runs straight along the road.</summary>
    [Fact]
    public void T16_SteeringOff_IgnoresTheWheel_AndPoseFollowsTheRoad()
    {
        var log = Script.Run(new Simulator(Golf_, Road.Flat(), Gear.Second, 20 / 3.6), 5,
            _ => new DriverInput(0, 0.2, 0, Gear.Second, SteeringWheelDeg: 300));

        Assert.All(log, s => Assert.False(s.Steering));
        Assert.All(log, s => Assert.Equal(s.PositionM, s.WorldX, 12));
        Assert.All(log, s => Assert.Equal(0, s.HeadingRad));
    }
}
