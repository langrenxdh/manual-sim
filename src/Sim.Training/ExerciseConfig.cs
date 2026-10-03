using System.Text.Json;
using System.Text.Json.Serialization;
using Sim.Core;

namespace Sim.Training;

/// <summary>Where an exercise starts. The host maps these to positions on its scene.</summary>
public enum StartPoint
{
    /// <summary>Beginning of the road, flat, at rest, neutral, idling.</summary>
    RoadStart,
    /// <summary>On the hill just before the stop line, at rest, neutral, idling, handbrake on.</summary>
    HillStart,
}

/// <summary>Quantities measured during an attempt (see <see cref="AttemptMetrics"/>).</summary>
public enum Metric
{
    ClutchSlipEnergyKJ,
    PeakJerkMps3,
    RollbackM,
    GrindingS,
    OverRevS,
    TimeS,
}

/// <summary>
/// All exercises and scoring tables, loaded from <c>config/exercises.json</c>
/// (docs/design.en.md, Practice and scoring). The caller passes the JSON text.
/// </summary>
public sealed record ExerciseConfig
{
    /// <summary>Low-pass applied to longitudinal acceleration before taking its rate of change.</summary>
    public required double JerkFilterHz { get; init; }
    public required GradeThresholds Grades { get; init; }
    public required CoachingParams Coaching { get; init; }
    public required ExerciseDef[] Exercises { get; init; }

    /// <summary>The vehicle file's conventions, plus enums written as camelCase strings.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(VehicleParams.JsonOptions)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static ExerciseConfig FromJson(string json)
    {
        var c = JsonSerializer.Deserialize<ExerciseConfig>(json, JsonOptions)
            ?? throw new JsonException("Empty exercises file.");
        c.Validate();
        return c;
    }

    public void Validate()
    {
        Require(JerkFilterHz > 0, "jerkFilterHz must be > 0");
        Require(Grades.A > Grades.B && Grades.B > Grades.C && Grades.C > 0 && Grades.A <= 100,
            "grades must satisfy 100 >= a > b > c > 0");
        Coaching.Validate();
        Require(Exercises.Length > 0, "at least one exercise is required");
        Require(Exercises.Select(e => e.Id).Distinct().Count() == Exercises.Length, "exercise ids must be unique");
        foreach (var e in Exercises) e.Validate();
    }

    public ExerciseDef Find(string id) =>
        Exercises.FirstOrDefault(e => e.Id == id) ?? throw new ArgumentException($"No exercise \"{id}\".");

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}

/// <summary>Minimum scores for grades A, B and C; anything below C is D.</summary>
public sealed record GradeThresholds
{
    public required double A { get; init; }
    public required double B { get; init; }
    public required double C { get; init; }
}

public sealed record ExerciseDef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>One line telling the driver what to do.</summary>
    public required string Goal { get; init; }
    public required StartPoint Start { get; init; }
    /// <summary>Forces hill-start assist on or off for this exercise; null keeps the vehicle setting.</summary>
    public bool? HillHold { get; init; }
    /// <summary>Lowest gear that counts at the finish (1-6); shifting higher before finishing is fine.</summary>
    public required int FinishGear { get; init; }
    public required double FinishMinSpeedKmh { get; init; }
    /// <summary>Distance forward from the start that must be covered (0 = none).</summary>
    public required double FinishMinDistanceM { get; init; }
    /// <summary>The finish conditions must hold continuously this long with the clutch locked.</summary>
    public required double FinishHoldS { get; init; }
    /// <summary>Rolling back further than this fails the attempt; null = no limit.</summary>
    public double? MaxRollbackM { get; init; }
    /// <summary>The attempt fails if not finished within this time.</summary>
    public required double TimeLimitS { get; init; }
    public required MetricScale[] Scoring { get; init; }

    internal void Validate()
    {
        string n = $"exercise \"{Id}\"";
        ExerciseConfig.Require(FinishGear is >= 1 and <= 6, $"{n}: finishGear must be 1-6");
        ExerciseConfig.Require(FinishMinSpeedKmh >= 0 && FinishMinDistanceM >= 0 && FinishHoldS >= 0,
            $"{n}: finish conditions must be >= 0");
        ExerciseConfig.Require(MaxRollbackM is null or > 0, $"{n}: maxRollbackM must be > 0");
        ExerciseConfig.Require(TimeLimitS > 0, $"{n}: timeLimitS must be > 0");
        ExerciseConfig.Require(Scoring.Select(s => s.Metric).Distinct().Count() == Scoring.Length,
            $"{n}: each metric may appear once");
        foreach (var s in Scoring)
        {
            ExerciseConfig.Require(s.Bad > s.Good, $"{n}: {s.Metric} bad must be above good");
            ExerciseConfig.Require(s.Weight >= 0, $"{n}: {s.Metric} weight must be >= 0");
        }
        ExerciseConfig.Require(Math.Abs(Scoring.Sum(s => s.Weight) - 100) < 1e-6, $"{n}: weights must add up to 100");
    }
}

/// <summary>
/// How one metric is scored: at or below <see cref="Good"/> no penalty, at or above <see cref="Bad"/>
/// the full <see cref="Weight"/>, linear in between.
/// </summary>
public sealed record MetricScale
{
    public required Metric Metric { get; init; }
    public required double Good { get; init; }
    public required double Bad { get; init; }
    public required double Weight { get; init; }
}
