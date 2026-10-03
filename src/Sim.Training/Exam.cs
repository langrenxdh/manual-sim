namespace Sim.Training;

/// <summary>
/// A driving exam (M8): a fixed sequence of exercises. Passed when every part is completed and the
/// average score reaches <see cref="PassAverage"/>. Defined in the <c>exams</c> section of <c>exercises.json</c>.
/// </summary>
public sealed record ExamDef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Exercise ids, in order.</summary>
    public required string[] Parts { get; init; }
    public required double PassAverage { get; init; }

    internal void Validate(ExerciseConfig config)
    {
        ExerciseConfig.Require(Parts.Length > 0, $"exam \"{Id}\": needs at least one part");
        foreach (var p in Parts)
            ExerciseConfig.Require(config.Exercises.Any(e => e.Id == p), $"exam \"{Id}\": unknown exercise \"{p}\"");
        ExerciseConfig.Require(PassAverage is > 0 and <= 100, $"exam \"{Id}\": passAverage must be in (0, 100]");
    }
}

/// <summary>One sitting of an exam: which part is next, the results so far and the verdict.</summary>
public sealed class ExamRun
{
    private readonly List<ScoreResult> _results = [];

    public ExamDef Exam { get; }
    public IReadOnlyList<ScoreResult> Results => _results;

    public ExamRun(ExamDef exam) => Exam = exam;

    /// <summary>The exercise id to drive next, or null when every part is done.</summary>
    public string? NextPart => _results.Count < Exam.Parts.Length ? Exam.Parts[_results.Count] : null;
    public int PartNumber => Math.Min(_results.Count + 1, Exam.Parts.Length);
    public bool Done => NextPart == null;

    /// <summary>Records the result of the current part; it must be for the expected exercise.</summary>
    public void Record(ScoreResult result)
    {
        if (Done) throw new InvalidOperationException("The exam is already finished.");
        if (result.ExerciseId != NextPart)
            throw new ArgumentException($"Expected a result for \"{NextPart}\", got \"{result.ExerciseId}\".", nameof(result));
        _results.Add(result);
    }

    /// <summary>Average score over the parts driven so far (failed parts count as 0).</summary>
    public double Average => _results.Count == 0 ? 0 : _results.Average(r => r.Failed ? 0 : r.Score);

    public IEnumerable<ScoreResult> FailedParts => _results.Where(r => r.Failed);

    /// <summary>Null until the exam is done; then whether every part completed and the average passed.</summary>
    public bool? Passed => Done ? !FailedParts.Any() && Average >= Exam.PassAverage : null;
}
