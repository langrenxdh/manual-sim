using Sim.Core;
using static Sim.Training.Tests.Attempt;

namespace Sim.Training.Tests;

/// <summary>
/// Scoring acceptance tests S6-S10 for the M6 exercises, with closed-loop scripted drivers
/// (docs/design.en.md, M6). Good technique must score at least a B; the named mistake must cost points.
/// </summary>
public class M6ExerciseTests
{
    private static readonly Scene Scene = Configs.Scene();

    // ---- S6 traffic queue -------------------------------------------------------------------------

    /// <summary>
    /// Follows a target speed = lead speed seen after <paramref name="reactS"/> + a gap correction; the
    /// clutch slides to the bite, then out over <paramref name="releaseS"/>; brakes to stop behind the lead.
    /// </summary>
    private static Func<double, SimState, DriverInput> QueueDriver(double reactS, double releaseS, double maxThrottle)
    {
        var lead = Configs.Exercises().Find("trafficQueue").Lead!;
        double leadPos = Scene.PositionOf(StartPoint.RoadStart) + lead.FrontAheadOfDriverM + lead.StartGapM, last = 0, clutch = 1;
        return (t, s) =>
        {
            double dt = t - last;
            last = t;
            leadPos += lead.SpeedAt(t) * dt;
            double gap = leadPos - (s.PositionM + lead.FrontAheadOfDriverM);
            double target = Math.Clamp(lead.SpeedAt(t - reactS) * 3.6 + 1.5 * (gap - 3.5), 0, 15);
            double v = s.SpeedKmh;
            if (target < 1)
            {
                clutch = 1;
                return new DriverInput(1, 0, v > 0.3 ? Math.Clamp(0.25 + (3 - gap) * 0.1, 0.1, 0.6) : 0.3, Gear.First);
            }
            clutch = clutch > 0.6 ? Math.Max(0.6, clutch - dt / 0.3) : Math.Max(0, clutch - dt * 0.6 / releaseS);
            double throttle = Math.Clamp(0.04 * (target - v) + 0.05, 0, maxThrottle);
            return new DriverInput(clutch, throttle, v > target + 3 ? 0.15 : 0, Gear.First);
        };
    }

    [Fact]
    public void S6_TrafficQueue_AttentiveSmoothFollower_ScoresAtLeastB()
    {
        var s = Run("trafficQueue", QueueDriver(reactS: 0.5, releaseS: 1.5, maxThrottle: 0.25));

        Assert.Equal(AttemptPhase.Completed, s.Phase);
        Assert.True(s.Result!.Score >= 75, $"score {s.Result.Score:F1}");
    }

    [Fact]
    public void S6b_TrafficQueue_LateReactions_LeaveTheBandAndScoreLower()
    {
        var good = Run("trafficQueue", QueueDriver(0.5, 1.5, 0.25)).Result!;
        var late = Run("trafficQueue", QueueDriver(2.5, 1.5, 0.25)).Result!;

        Assert.False(late.Failed);
        Assert.True(late.Metrics.GapOutsideBandS > good.Metrics.GapOutsideBandS + 5);
        Assert.True(late.Score < good.Score - 25, $"late {late.Score:F1} vs good {good.Score:F1}");
    }

    // ---- S7 stop on the line, then hill start ----------------------------------------------------------

    /// <summary>
    /// Pulls away, drives in 2nd, brakes (easing off near standstill) to stop with the front
    /// <paramref name="shortOfLineM"/> before the line, then a proper hill start with <paramref name="pullThrottle"/>.
    /// With <paramref name="brakes"/> false it never brakes and drives over the line.
    /// </summary>
    private static Func<double, SimState, DriverInput> StopAndGo(double shortOfLineM, double pullThrottle, bool brakes = true)
    {
        var line = Configs.Exercises().Find("stopAndHillStart").StopLine!;
        string phase = "go";
        double phaseT = 0;
        return (t, s) =>
        {
            double toStop = (Scene.StopLineM - shortOfLineM) - (s.PositionM + line.FrontAheadOfDriverM);
            if (phase == "go")
            {
                if (t < 3.0) return new DriverInput(Ramp(t, 0.5, 2.0, 1, 0), Throttle(t, 0.5, 0.15), 0, Gear.First);
                if (t < 3.5) return new DriverInput(1, 0, 0, Gear.Neutral);
                if (t < 3.9) return new DriverInput(1, 0, 0, Gear.Second);
                if (!brakes || toStop > Math.Max(6, s.SpeedMps * s.SpeedMps / 4.0 + 3))
                    return new DriverInput(Ramp(t, 3.9, 0.8, 1, 0), s.SpeedKmh < 35 ? 0.35 : 0.1, 0, Gear.Second);
                phase = "brake";
                phaseT = t;
            }
            if (phase == "brake")
            {
                // Deceleration still needed beyond the slope and rolling resistance (~1.1 m/s^2), eased near standstill.
                double need = s.SpeedMps * s.SpeedMps / (2 * Math.Max(toStop, 0.05)) - 1.1;
                double brake = s.SpeedKmh < 0.5 ? 0.4 : Math.Clamp(need / 9.8, 0.02, 0.9) * Math.Clamp(s.SpeedKmh / 6, 0, 1);
                if (!(s.SpeedMps < 0.02 && t - phaseT > 1))
                    return new DriverInput(s.SpeedKmh < 15 ? 1 : 0, 0, brake, s.SpeedKmh < 15 ? Gear.Neutral : Gear.Second);
                phase = "stopped";
                phaseT = t;
            }
            double tt = t - phaseT;
            if (tt < 1.5) return new DriverInput(1, 0, 0.5, tt < 0.5 ? Gear.Neutral : Gear.First);
            double clutch = tt < 2.1 ? Ramp(tt, 1.5, 0.6, 1, 0.55) : tt < 2.6 ? 0.55 : Ramp(tt, 2.6, 3, 0.55, 0);
            return new DriverInput(clutch, Throttle(tt, 1.5, pullThrottle), 0, Gear.First);
        };
    }

    [Fact]
    public void S7_StopOnTheLineThenPullAway_Cleanly_ScoresAtLeastB()
    {
        var s = Run("stopAndHillStart", StopAndGo(shortOfLineM: 0.5, pullThrottle: 0.3));

        Assert.Equal(AttemptPhase.Completed, s.Phase);
        Assert.True(s.Metrics.StopErrorM < 0.3, $"stop error {s.Metrics.StopErrorM:F2} m");
        Assert.True(s.Result!.Score >= 75, $"score {s.Result.Score:F1}");
    }

    [Fact]
    public void S7b_StoppingTwoMetresShort_CostsPoints()
    {
        var ideal = Run("stopAndHillStart", StopAndGo(0.5, 0.3)).Result!;
        var shortStop = Run("stopAndHillStart", StopAndGo(2.5, 0.3)).Result!;

        Assert.InRange(shortStop.Metrics.StopErrorM, 1.5, 2.5);
        Assert.True(shortStop.Score < ideal.Score - 8, $"short {shortStop.Score:F1} vs ideal {ideal.Score:F1}");
    }

    [Fact]
    public void S7c_DrivingOverTheLine_Fails()
    {
        var s = Run("stopAndHillStart", StopAndGo(0.5, 0.3, brakes: false));

        Assert.Equal(AttemptPhase.Failed, s.Phase);
        Assert.Equal("ran the stop line", s.FailReason);
    }

    // ---- S8 downhill -------------------------------------------------------------------------------

    /// <summary>
    /// Pulls away, then drives down in <paramref name="gear"/> (2nd straight away, or 3rd from 30 km/h),
    /// braking only above <paramref name="brakeAboveKmh"/>. With <paramref name="coastFirstHalf"/> it
    /// rolls the first half of the descent in neutral, then selects the gear again.
    /// </summary>
    private static Func<double, SimState, DriverInput> Downhill(Gear gear, double brakeAboveKmh, bool coastFirstHalf = false)
    {
        double midDescent = (Scene.DownhillStartM + Scene.DownhillEndM) / 2;
        double engagedAt = -1;
        return (t, s) =>
        {
            if (t < 3.0) return new DriverInput(Ramp(t, 0.5, 2.0, 1, 0), Throttle(t, 0.5, 0.15), 0, Gear.First);
            if (t < 3.5) return new DriverInput(1, 0, 0, Gear.Neutral);
            double brake = s.SpeedKmh > brakeAboveKmh ? Math.Clamp((s.SpeedKmh - brakeAboveKmh) * 0.05, 0, 0.5) : 0;
            bool coasting = coastFirstHalf && s.Grade < -0.02 && s.PositionM < midDescent;
            if (coasting) return new DriverInput(1, 0, brake, Gear.Neutral);
            if (gear == Gear.Third && s.SpeedKmh < 30 && engagedAt < 0)
                return new DriverInput(t < 3.9 ? 1 : Ramp(t, 3.9, 0.8, 1, 0), 0.35, 0, Gear.Second);
            if (engagedAt < 0 || (coastFirstHalf && s.PositionM >= midDescent && engagedAt < 4)) engagedAt = t;
            double tt = t - engagedAt;
            double throttle = s.SpeedKmh < brakeAboveKmh - 10 && s.Grade > -0.01 ? 0.3 : 0;
            if (tt < 0.8) return new DriverInput(1, 0, brake, tt < 0.4 ? Gear.Neutral : gear);
            return new DriverInput(Ramp(tt, 0.8, 0.8, 1, 0), throttle, brake, gear);
        };
    }

    [Fact]
    public void S8_DownhillInSecond_EngineBrakingHoldsTheLimit_WithoutTheFootBrake()
    {
        var s = Run("downhill", Downhill(Gear.Second, brakeAboveKmh: 42));

        Assert.Equal(AttemptPhase.Completed, s.Phase);
        Assert.Equal(0, s.Metrics.OverSpeedS);
        Assert.True(s.Metrics.BrakeS < 1, $"brake {s.Metrics.BrakeS:F1} s");
        Assert.True(s.Result!.Score >= 90, $"score {s.Result.Score:F1}");
    }

    [Fact]
    public void S8b_LettingItRunPastTheLimit_ScoresLower()
    {
        var good = Run("downhill", Downhill(Gear.Second, 42)).Result!;
        var fast = Run("downhill", Downhill(Gear.Third, 55)).Result!;

        Assert.True(fast.Metrics.OverSpeedS > 1);
        Assert.True(fast.Score < good.Score - 10, $"fast {fast.Score:F1} vs good {good.Score:F1}");
    }

    [Fact]
    public void S8c_CoastingInNeutral_IsCountedAndCostsPoints()
    {
        var good = Run("downhill", Downhill(Gear.Second, 42)).Result!;
        var coast = Run("downhill", Downhill(Gear.Second, 42, coastFirstHalf: true)).Result!;

        Assert.True(coast.Metrics.CoastingS > good.Metrics.CoastingS + 5, $"coasting {coast.Metrics.CoastingS:F1} s");
        Assert.True(coast.Score < good.Score - 15, $"coast {coast.Score:F1} vs good {good.Score:F1}");
    }

    // ---- S9 reverse uphill --------------------------------------------------------------------------

    private static DriverInput Reverse(double t, double throttle, double releaseS, Gear gear = Gear.Reverse)
    {
        double clutch = t < 1.0 ? 1 : t < 1.6 ? Ramp(t, 1.0, 0.6, 1, 0.55) : t < 2.1 ? 0.55 : Ramp(t, 2.1, releaseS, 0.55, 0);
        return new DriverInput(clutch, Throttle(t, 1.0, throttle), 0, t < 0.5 ? Gear.Neutral : gear);
    }

    [Fact]
    public void S9_ReverseUphill_Gently_ScoresAtLeastB()
    {
        var s = Run("reverseUphill", t => Reverse(t, throttle: 0.3, releaseS: 3));

        Assert.Equal(AttemptPhase.Completed, s.Phase);
        Assert.True(s.Result!.Score >= 75, $"score {s.Result.Score:F1}");
    }

    [Fact]
    public void S9b_ReverseUphill_HeavyThrottleAndQuickClutch_ScoresLower()
    {
        var gentle = Run("reverseUphill", t => Reverse(t, 0.3, 3)).Result!;
        var harsh = Run("reverseUphill", t => Reverse(t, 0.6, 1.5)).Result!;

        Assert.True(harsh.Score < gentle.Score - 25, $"harsh {harsh.Score:F1} vs gentle {gentle.Score:F1}");
    }

    [Fact]
    public void S9c_DrivingForwardInsteadOfReversing_CountsAsRollingTheWrongWayAndFails()
    {
        var s = Run("reverseUphill", t => Reverse(t, 0.3, 3, gear: Gear.First));

        Assert.Equal(AttemptPhase.Failed, s.Phase);
        Assert.StartsWith("rolled back", s.FailReason);
    }

    // ---- S10 rev-matched downshift -----------------------------------------------------------------------

    /// <summary>1st, 2nd, 3rd to 50 km/h; then clutch in, a throttle blip of <paramref name="blip"/>, 2nd, release.</summary>
    private static Func<double, SimState, DriverInput> Downshift(double blip, double releaseS)
    {
        string phase = "1";
        double t0 = 0;
        return (t, s) =>
        {
            if (phase == "1")
            {
                if (t < 2.5) return new DriverInput(Ramp(t, 0.5, 1.5, 1, 0), Throttle(t, 0.5, 0.5), 0, Gear.First);
                if (s.EngineRpm < 3000) return new DriverInput(0, 0.5, 0, Gear.First);
                phase = "12"; t0 = t;
            }
            double tt = t - t0;
            if (phase == "12")
            {
                if (tt < 0.8) return new DriverInput(1, 0, 0, tt < 0.4 ? Gear.Neutral : Gear.Second);
                if (tt < 1.4) return new DriverInput(Ramp(tt, 0.8, 0.6, 1, 0), 0.4, 0, Gear.Second);
                if (s.EngineRpm < 3000) return new DriverInput(0, 0.5, 0, Gear.Second);
                phase = "23"; t0 = t; tt = 0;
            }
            if (phase == "23")
            {
                if (tt < 0.8) return new DriverInput(1, 0, 0, tt < 0.4 ? Gear.Neutral : Gear.Third);
                if (tt < 1.4) return new DriverInput(Ramp(tt, 0.8, 0.6, 1, 0), 0.4, 0, Gear.Third);
                if (s.SpeedKmh < 50) return new DriverInput(0, 0.5, 0, Gear.Third);
                phase = "32"; t0 = t; tt = 0;
            }
            if (tt < 0.9) return new DriverInput(1, tt is > 0.2 and < 0.6 ? blip : 0, 0, tt < 0.4 ? Gear.Neutral : Gear.Second);
            return new DriverInput(Ramp(tt, 0.9, releaseS, 1, 0), 0.1, 0, Gear.Second);
        };
    }

    [Fact]
    public void S10_DownshiftWithABlip_MatchesRevs_ScoresAtLeastA()
    {
        var s = Run("revMatchDownshift", Downshift(blip: 0.6, releaseS: 0.5));

        Assert.Equal(AttemptPhase.Completed, s.Phase);
        Assert.True(s.Metrics.RevMatchErrorRpm < 600, $"rev mismatch {s.Metrics.RevMatchErrorRpm:F0} rpm");
        Assert.True(s.Result!.Score >= 90, $"score {s.Result.Score:F1}");
    }

    [Fact]
    public void S10b_DownshiftWithoutABlip_LurchesAndScoresLower()
    {
        var blip = Run("revMatchDownshift", Downshift(0.6, 0.5)).Result!;
        var none = Run("revMatchDownshift", Downshift(0.0, 0.5)).Result!;

        Assert.True(none.Metrics.RevMatchErrorRpm > 1500, $"rev mismatch {none.Metrics.RevMatchErrorRpm:F0} rpm");
        Assert.True(none.Metrics.DownshiftJerkMps3 > blip.Metrics.DownshiftJerkMps3);
        Assert.True(none.Score < blip.Score - 30, $"no blip {none.Score:F1} vs blip {blip.Score:F1}");
    }
}
