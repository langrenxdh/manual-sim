namespace Sim.Training.Tests;

public class ScorerTests
{
    private static readonly MetricScale Scale = new() { Metric = Metric.ClutchSlipEnergyKJ, Good = 10, Bad = 30, Weight = 40 };

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 0)]
    [InlineData(20, 20)]
    [InlineData(30, 40)]
    [InlineData(1000, 40)]
    public void Penalty_IsZeroAtGood_FullWeightAtBad_LinearBetween_Clamped(double value, double expected)
    {
        Assert.Equal(expected, Scorer.PenaltyPoints(Scale, value), 9);
    }

    [Theory]
    [InlineData(95, 'A')]
    [InlineData(90, 'A')]
    [InlineData(80, 'B')]
    [InlineData(60, 'C')]
    [InlineData(10, 'D')]
    public void Grades_FollowThresholds(double score, char grade)
    {
        Assert.Equal(grade, Scorer.GradeFor(Configs.Exercises().Grades, score));
    }

    [Fact]
    public void ShippedConfig_IsValid_AndEveryExerciseWeighsTo100()
    {
        var config = Configs.Exercises();
        Assert.Equal(4, config.Exercises.Length);
        Assert.All(config.Exercises, e => Assert.Equal(100, e.Scoring.Sum(s => s.Weight), 9));
    }

    [Fact]
    public void Config_RejectsWeightsThatDoNotAddUpTo100()
    {
        var config = Configs.Exercises();
        var broken = config with
        {
            Exercises = [config.Exercises[0] with { Scoring = [Scale] }],
        };
        Assert.Throws<ArgumentException>(broken.Validate);
    }

    [Fact]
    public void ApplyTo_OnlyChangesHillHoldEnabled()
    {
        var golf = Configs.Golf();
        var config = Configs.Exercises();
        var off = ExerciseSession.ApplyTo(config.Find("hillStartHandbrake"), golf);
        var unchanged = ExerciseSession.ApplyTo(config.Find("flatPullAway"), golf);

        Assert.False(off.HillHold.Enabled);
        Assert.Equal(golf.HillHold with { Enabled = false }, off.HillHold);
        Assert.Equal(golf.Engine, off.Engine);
        Assert.Same(golf, unchanged);
    }
}
