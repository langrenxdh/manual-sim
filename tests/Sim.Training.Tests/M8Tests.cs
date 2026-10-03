using Sim.Core;
using static Sim.Training.Tests.Attempt;

namespace Sim.Training.Tests;

/// <summary>M8: exam verdicts and teaching-mode cues.</summary>
public class M8Tests
{
    private static ScoreResult Result(string id, double score, bool failed = false) =>
        new(id, failed ? 0 : score, 'B', failed, failed ? "stalled" : null, default, [], []);

    private static ExamDef Exam => Configs.Exercises().Exams.Single(e => e.Id == "drivingTest");

    [Fact]
    public void Exam_AllPartsCompletedAboveTheAverage_Passes()
    {
        var run = new ExamRun(Exam);
        foreach (var part in Exam.Parts) run.Record(Result(part, 85));

        Assert.True(run.Done);
        Assert.True(run.Passed);
        Assert.Equal(85, run.Average, 9);
    }

    [Fact]
    public void Exam_OneFailedPart_FailsEvenWithAHighAverage()
    {
        var run = new ExamRun(Exam);
        foreach (var part in Exam.Parts) run.Record(Result(part, 100, failed: part == Exam.Parts[1]));

        Assert.False(run.Passed);
        Assert.Single(run.FailedParts);
    }

    [Fact]
    public void Exam_AverageBelowThePassMark_Fails()
    {
        var run = new ExamRun(Exam);
        foreach (var part in Exam.Parts) run.Record(Result(part, Exam.PassAverage - 5));

        Assert.False(run.Passed);
    }

    [Fact]
    public void Exam_IsUndecidedUntilDone_AndOnlyAcceptsTheExpectedPart()
    {
        var run = new ExamRun(Exam);
        run.Record(Result(Exam.Parts[0], 90));

        Assert.Null(run.Passed);
        Assert.Equal(2, run.PartNumber);
        Assert.Throws<ArgumentException>(() => run.Record(Result(Exam.Parts[0], 90)));
    }

    private static List<string> CuesDuring(string exerciseId, Func<double, SimState, DriverInput> driver, double seconds)
    {
        var cues = new CueEngine(CueConfig.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", "cues.json"))),
            Configs.Golf());
        var fired = new List<string>();
        Run(exerciseId, (t, s) =>
        {
            if (cues.Update(s, Simulator.StepS) is { } cue) fired.Add(cue.Id);
            return driver(t, s);
        }, seconds);
        return fired;
    }

    [Fact]
    public void Cues_GentlePullAway_SaysBitePointOnce_AndNothingElse()
    {
        var fired = CuesDuring("flatPullAway", (t, _) => new DriverInput(Ramp(t, 0.5, 2, 1, 0), 0, 0, Gear.First), 6);

        Assert.Equal(["bite"], fired);
    }

    [Fact]
    public void Cues_ClutchDump_WarnsThatTheEngineNeedsThrottle()
    {
        var fired = CuesDuring("flatPullAway", (t, _) => new DriverInput(Ramp(t, 0.5, 0.4, 1, 0), 0, 0, Gear.First), 3);

        Assert.Contains("throttle", fired);
    }

    [Fact]
    public void Cues_HandbrakeReleasedOnTheHillWithoutThrottle_SaysRollingBack()
    {
        var fired = CuesDuring("hillStartHandbrake", (t, _) => new DriverInput(1, 0, 0, Gear.First, Handbrake: t < 0.5), 3);

        Assert.Contains("rollback", fired);
    }
}
