namespace Sim.Training;

/// <summary>One metric's contribution to a score.</summary>
public sealed record Penalty(Metric Metric, double Value, double Points, double Weight);

/// <summary>The outcome of one attempt. Immutable, so it can be handed across threads.</summary>
public sealed record ScoreResult(
    string ExerciseId,
    double Score,
    char Grade,
    bool Failed,
    string? FailReason,
    AttemptMetrics Metrics,
    IReadOnlyList<Penalty> Penalties)
{
    /// <summary>The metric that cost the most points: the main thing to work on. Null if none cost any.</summary>
    public Penalty? Biggest => Penalties.Where(p => p.Points > 0).MaxBy(p => p.Points);
}

/// <summary>Score = 100 - sum of penalties (docs/design.en.md, Practice and scoring).</summary>
public static class Scorer
{
    /// <summary>Points lost on one metric: 0 at or below good, the full weight at or above bad, linear between.</summary>
    public static double PenaltyPoints(MetricScale scale, double value) =>
        scale.Weight * Math.Clamp((value - scale.Good) / (scale.Bad - scale.Good), 0, 1);

    public static ScoreResult Score(ExerciseConfig config, ExerciseDef exercise, AttemptMetrics m, bool failed, string? reason)
    {
        var penalties = exercise.Scoring
            .Select(s => new Penalty(s.Metric, m.Get(s.Metric), PenaltyPoints(s, m.Get(s.Metric)), s.Weight))
            .ToList();
        // A failed attempt (stall, rollback, time limit) scores 0; the penalties still show what went wrong.
        double score = failed ? 0 : Math.Max(0, 100 - penalties.Sum(p => p.Points));
        return new ScoreResult(exercise.Id, score, GradeFor(config.Grades, score), failed, reason, m, penalties);
    }

    public static char GradeFor(GradeThresholds g, double score) =>
        score >= g.A ? 'A' : score >= g.B ? 'B' : score >= g.C ? 'C' : 'D';
}
