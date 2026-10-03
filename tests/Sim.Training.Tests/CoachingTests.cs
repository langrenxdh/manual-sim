using Sim.Core;
using static Sim.Training.Tests.Attempt;

namespace Sim.Training.Tests;

/// <summary>M5 coaching: attempt traces, ghost comparison and the progress summary.</summary>
public class CoachingTests
{
    private static CoachingParams Coaching => Configs.Exercises().Coaching;

    private static DriverInput PullAway(double t, double releaseS) =>
        new(Ramp(t, 0.5, releaseS, 1, 0), 0, 0, Gear.First);

    [Fact]
    public void Attempt_RecordsA100HzTraceOfPedalsRpmAndSpeed()
    {
        var r = Run("flatPullAway", t => PullAway(t, 2)).Result!;

        Assert.InRange(r.Trace.Count, (int)(r.Metrics.ElapsedS / Trace.IntervalS) - 2, (int)(r.Metrics.ElapsedS / Trace.IntervalS) + 2);
        Assert.Equal(1, r.Trace[0].Clutch, 3);                  // starts with the clutch down
        Assert.True(r.Trace[^1].Clutch < 0.05);                 // ends released
        Assert.True(r.Trace[^1].SpeedKmh >= 5);
        Assert.All(r.Trace.Zip(r.Trace.Skip(1)), p => Assert.Equal(Trace.IntervalS, p.Second.TimeS - p.First.TimeS, 3));
    }

    [Fact]
    public void Ghost_SameDriving_HasNoDivergence()
    {
        var a = Run("flatPullAway", t => PullAway(t, 2)).Result!;
        var b = Run("flatPullAway", t => PullAway(t, 2)).Result!;

        Assert.Null(GhostComparison.First(a.Trace, b.Trace, Coaching));
    }

    [Fact]
    public void Ghost_FasterClutchRelease_DivergesOnTheClutch_EarlyInTheRelease()
    {
        var best = Run("flatPullAway", t => PullAway(t, 3)).Result!;
        var hasty = Run("flatPullAway", t => PullAway(t, 1.5)).Result!;

        var d = GhostComparison.First(hasty.Trace, best.Trace, Coaching);

        Assert.NotNull(d);
        Assert.Equal("clutch", d.Pedal);
        Assert.True(d.Current < d.Best, "the hasty attempt has the clutch further up (released)");
        Assert.InRange(d.TimeS, 0.5, 2.0);
    }

    [Fact]
    public void Progress_SummarisesBestRecentTrendAndMostCommonIssue()
    {
        var t0 = new DateTime(2026, 10, 3, 12, 0, 0);
        var history = new List<AttemptRecord>
        {
            new(t0.AddMinutes(0), "flatPullAway", 0, true, "stalled"),
            new(t0.AddMinutes(1), "flatPullAway", 60, false, "PeakJerkMps3"),
            new(t0.AddMinutes(2), "flatPullAway", 70, false, "PeakJerkMps3"),
            new(t0.AddMinutes(3), "flatPullAway", 80, false, "ClutchSlipEnergyKJ"),
            new(t0.AddMinutes(4), "flatPullAway", 90, false, null),
            new(t0.AddMinutes(5), "hillStartAssist", 99, false, null),
        };

        var p = Progress.Summarise("flatPullAway", history, Coaching);

        Assert.Equal(5, p.Attempts);
        Assert.Equal(4, p.Completed);
        Assert.Equal(90, p.Best);
        Assert.Equal(60, p.RecentAverage!.Value, 9);            // (0 + 60 + 70 + 80 + 90) / 5, failures count as 0
        Assert.True(p.TrendPerAttempt > 0, "scores are improving");
        Assert.Equal("PeakJerkMps3", p.MostCommonIssue);
        Assert.Equal([0, 60, 70, 80, 90], p.RecentScores);
    }

    [Fact]
    public void Progress_WithNoAttempts_HasNothingToReport()
    {
        var p = Progress.Summarise("flatPullAway", [], Coaching);

        Assert.Equal(0, p.Attempts);
        Assert.Null(p.Best);
        Assert.Null(p.RecentAverage);
        Assert.Null(p.TrendPerAttempt);
    }

    [Theory]
    [InlineData(new double[] { 1, 2, 3, 4 }, 1)]
    [InlineData(new double[] { 5, 5, 5 }, 0)]
    [InlineData(new double[] { 9, 6, 3 }, -3)]
    public void Slope_IsPointsPerAttempt(double[] ys, double expected)
    {
        Assert.Equal(expected, Progress.Slope(ys), 9);
    }
}
