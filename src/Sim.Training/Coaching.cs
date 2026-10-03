namespace Sim.Training;

/// <summary>One sample of an attempt's trace (pedals 0 = up, 1 = floor), recorded at <see cref="Trace.IntervalS"/>.</summary>
public readonly record struct TraceSample(float TimeS, float Clutch, float Throttle, float Brake, float EngineRpm, float SpeedKmh);

public static class Trace
{
    /// <summary>Attempt traces are sampled at 100 Hz: fine enough to see a pedal move, small enough to keep.</summary>
    public const double IntervalS = 0.01;
}

/// <summary>Thresholds for coaching feedback, from the <c>coaching</c> section of <c>config/exercises.json</c>.</summary>
public sealed record CoachingParams
{
    /// <summary>A pedal counts as different from the ghost when the gap exceeds this (fraction of travel)...</summary>
    public required double DivergencePedalGap { get; init; }
    /// <summary>...for at least this long.</summary>
    public required double DivergenceHoldS { get; init; }
    /// <summary>Attempts averaged for "recently".</summary>
    public required int RecentAttempts { get; init; }
    /// <summary>Attempts used for the trend line and the most common issue.</summary>
    public required int TrendAttempts { get; init; }

    internal void Validate()
    {
        ExerciseConfig.Require(DivergencePedalGap is > 0 and < 1, "coaching.divergencePedalGap must be in (0, 1)");
        ExerciseConfig.Require(DivergenceHoldS > 0, "coaching.divergenceHoldS must be > 0");
        ExerciseConfig.Require(RecentAttempts >= 1 && TrendAttempts >= 3, "coaching: recentAttempts >= 1 and trendAttempts >= 3");
    }
}

/// <summary>Where an attempt first parted from the ghost (best attempt).</summary>
public sealed record Divergence(double TimeS, string Pedal, double Current, double Best);

/// <summary>Ghost comparison (docs/design.en.md, M5): compares an attempt's trace with the best attempt's.</summary>
public static class GhostComparison
{
    /// <summary>
    /// The first moment, counted from the attempt start, where the clutch or throttle pedal stayed further than
    /// <see cref="CoachingParams.DivergencePedalGap"/> from the ghost for <see cref="CoachingParams.DivergenceHoldS"/>.
    /// Null if the attempt followed the ghost throughout their common length.
    /// </summary>
    public static Divergence? First(IReadOnlyList<TraceSample> current, IReadOnlyList<TraceSample> ghost, CoachingParams c)
    {
        int n = Math.Min(current.Count, ghost.Count);
        int needed = Math.Max(1, (int)Math.Round(c.DivergenceHoldS / Trace.IntervalS));
        int clutchRun = 0, throttleRun = 0;
        for (int i = 0; i < n; i++)
        {
            clutchRun = Math.Abs(current[i].Clutch - ghost[i].Clutch) > c.DivergencePedalGap ? clutchRun + 1 : 0;
            throttleRun = Math.Abs(current[i].Throttle - ghost[i].Throttle) > c.DivergencePedalGap ? throttleRun + 1 : 0;
            if (clutchRun >= needed) return At(current, ghost, i - needed + 1, "clutch", s => s.Clutch);
            if (throttleRun >= needed) return At(current, ghost, i - needed + 1, "throttle", s => s.Throttle);
        }
        return null;
    }

    private static Divergence At(IReadOnlyList<TraceSample> current, IReadOnlyList<TraceSample> ghost, int i,
        string pedal, Func<TraceSample, float> get) =>
        new(current[i].TimeS, pedal, get(current[i]), get(ghost[i]));
}

/// <summary>One finished attempt as kept in the score history.</summary>
/// <param name="MainIssue">The failure reason, or the metric that cost the most points, or null for a clean run.</param>
public sealed record AttemptRecord(DateTime Time, string ExerciseId, double Score, bool Failed, string? MainIssue);

/// <summary>How one exercise is going over time.</summary>
public sealed record ExerciseProgress(
    string ExerciseId,
    int Attempts,
    int Completed,
    double? Best,
    double? RecentAverage,
    double? TrendPerAttempt,
    string? MostCommonIssue,
    IReadOnlyList<double> RecentScores);

/// <summary>Progress view (docs/design.en.md, M5). Failed attempts count as 0 points.</summary>
public static class Progress
{
    public static ExerciseProgress Summarise(string exerciseId, IEnumerable<AttemptRecord> history, CoachingParams c)
    {
        var attempts = history.Where(a => a.ExerciseId == exerciseId).OrderBy(a => a.Time).ToList();
        var scores = attempts.Select(a => a.Failed ? 0 : a.Score).ToList();
        var completed = attempts.Where(a => !a.Failed).ToList();
        var trendWindow = scores.TakeLast(c.TrendAttempts).ToList();

        return new ExerciseProgress(
            exerciseId,
            attempts.Count,
            completed.Count,
            completed.Count > 0 ? completed.Max(a => a.Score) : null,
            scores.Count > 0 ? scores.TakeLast(c.RecentAttempts).Average() : null,
            trendWindow.Count >= 3 ? Slope(trendWindow) : null,
            attempts.TakeLast(c.TrendAttempts).Select(a => a.MainIssue).OfType<string>()
                .GroupBy(i => i).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(),
            trendWindow);
    }

    /// <summary>Least-squares slope of the scores against attempt number: points gained per attempt.</summary>
    public static double Slope(IReadOnlyList<double> ys)
    {
        int n = ys.Count;
        double meanX = (n - 1) / 2.0, meanY = ys.Average();
        double num = 0, den = 0;
        for (int i = 0; i < n; i++)
        {
            num += (i - meanX) * (ys[i] - meanY);
            den += (i - meanX) * (i - meanX);
        }
        return den == 0 ? 0 : num / den;
    }
}
