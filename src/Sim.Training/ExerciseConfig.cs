using System.Text.Json;
using System.Text.Json.Serialization;
using Sim.Core;

namespace Sim.Training;

/// <summary>Where an exercise starts. <see cref="Scene.PositionOf"/> maps these to road positions.</summary>
public enum StartPoint
{
    /// <summary>Beginning of the road, flat, at rest, neutral, idling.</summary>
    RoadStart,
    /// <summary>On the hill just before the stop line, at rest, neutral, idling, handbrake on.</summary>
    HillStart,
    /// <summary>On the plateau after the hill, before the downhill (M6).</summary>
    PlateauStart,
    /// <summary>Just past the bottom of the downhill, which rises behind the car (M6, reversing uphill).</summary>
    DownhillBottom,
}

/// <summary>Which way the exercise wants the car to travel along the road.</summary>
public enum TravelDirection { Forward, Reverse }

/// <summary>Quantities measured during an attempt (see <see cref="AttemptMetrics"/>).</summary>
public enum Metric
{
    ClutchSlipEnergyKJ,
    PeakJerkMps3,
    RollbackM,
    GrindingS,
    OverRevS,
    TimeS,
    /// <summary>Time with the gap to the car ahead outside its comfortable band (M6 queue).</summary>
    GapOutsideBandS,
    /// <summary>How far the car's front stopped from the ideal point before the stop line (M6).</summary>
    StopErrorM,
    /// <summary>Time above the exercise's speed limit (M6 downhill).</summary>
    OverSpeedS,
    /// <summary>Time with the brake pedal pressed (M6 downhill: use the engine instead).</summary>
    BrakeS,
    /// <summary>Time rolling in neutral or with the clutch down (M6 downhill).</summary>
    CoastingS,
    /// <summary>Clutch slip speed at the moment the lower gear starts to bite (M6 downshift).</summary>
    RevMatchErrorRpm,
    /// <summary>Peak jerk from the downshift onwards, not counting the launch (M6 downshift).</summary>
    DownshiftJerkMps3,
}

/// <summary>
/// All exercises and scoring tables, loaded from <c>config/exercises.json</c>
/// (docs/design.en.md, Practice and scoring). The caller passes the JSON text.
/// </summary>
public sealed record ExerciseConfig
{
    /// <summary>Low-pass applied to longitudinal acceleration before taking its rate of change.</summary>
    public required double JerkFilterHz { get; init; }
    /// <summary>Pedal position above which a pedal counts as pressed (brake use, coasting).</summary>
    public required double PedalPressedAbove { get; init; }
    /// <summary>Rolling faster than this in neutral or with the clutch disengaged counts as coasting.</summary>
    public required double CoastingMinSpeedKmh { get; init; }
    /// <summary>Clutch engagement below which the drivetrain counts as disconnected (coasting).</summary>
    public required double CoastingMaxEngagement { get; init; }
    /// <summary>Speed below which the car counts as stopped (stop line, queue).</summary>
    public required double StoppedBelowKmh { get; init; }
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
        Require(PedalPressedAbove is > 0 and < 1, "pedalPressedAbove must be in (0, 1)");
        Require(CoastingMinSpeedKmh > 0 && StoppedBelowKmh > 0, "coastingMinSpeedKmh and stoppedBelowKmh must be > 0");
        Require(CoastingMaxEngagement is > 0 and < 1, "coastingMaxEngagement must be in (0, 1)");
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
    public TravelDirection Direction { get; init; } = TravelDirection.Forward;
    /// <summary>Forces hill-start assist on or off for this exercise; null keeps the vehicle setting.</summary>
    public bool? HillHold { get; init; }
    /// <summary>
    /// Lowest gear that counts at the finish (1-6; shifting higher before finishing is fine), or -1 for
    /// reverse. With a <see cref="Downshift"/> the finish needs exactly its target gear.
    /// </summary>
    public required int FinishGear { get; init; }
    public required double FinishMinSpeedKmh { get; init; }
    /// <summary>Distance in the travel direction that must be covered (from the stop, with a stop line).</summary>
    public required double FinishMinDistanceM { get; init; }
    /// <summary>The finish conditions must hold continuously this long.</summary>
    public required double FinishHoldS { get; init; }
    /// <summary>Moving against the travel direction further than this fails the attempt; null = no limit.</summary>
    public double? MaxRollbackM { get; init; }
    /// <summary>The attempt fails if not finished within this time.</summary>
    public required double TimeLimitS { get; init; }
    /// <summary>Scored speed limit (over-speed metric); null = none.</summary>
    public double? SpeedLimitKmh { get; init; }
    public LeadCarDef? Lead { get; init; }
    public StopLineDef? StopLine { get; init; }
    public DownshiftDef? Downshift { get; init; }
    public required MetricScale[] Scoring { get; init; }

    internal void Validate()
    {
        string n = $"exercise \"{Id}\"";
        ExerciseConfig.Require(FinishGear is -1 or (>= 1 and <= 6), $"{n}: finishGear must be 1-6, or -1 for reverse");
        ExerciseConfig.Require((FinishGear == -1) == (Direction == TravelDirection.Reverse),
            $"{n}: reverse travel and finishGear -1 go together");
        ExerciseConfig.Require(FinishMinSpeedKmh >= 0 && FinishMinDistanceM >= 0 && FinishHoldS >= 0,
            $"{n}: finish conditions must be >= 0");
        ExerciseConfig.Require(MaxRollbackM is null or > 0, $"{n}: maxRollbackM must be > 0");
        ExerciseConfig.Require(TimeLimitS > 0, $"{n}: timeLimitS must be > 0");
        ExerciseConfig.Require(SpeedLimitKmh is null or > 0, $"{n}: speedLimitKmh must be > 0");
        Lead?.Validate(n);
        StopLine?.Validate(n);
        Downshift?.Validate(n);
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
/// A car ahead that drives a scripted stop-and-go pattern (kinematic: it does not use the physics).
/// The gap is bumper to bumper, measured from the driver's position plus <see cref="FrontAheadOfDriverM"/>.
/// </summary>
public sealed record LeadCarDef
{
    public required double StartGapM { get; init; }
    /// <summary>Closer than this counts as touching the car ahead and fails the attempt.</summary>
    public required double MinGapM { get; init; }
    /// <summary>Comfortable following gap; time outside it is the <see cref="Metric.GapOutsideBandS"/> metric.</summary>
    public required double BandMinM { get; init; }
    public required double BandMaxM { get; init; }
    public required double FrontAheadOfDriverM { get; init; }
    /// <summary>[time s, speed km/h] points, linear in between; the lead stops after the last point.</summary>
    public required double[][] SpeedProfile { get; init; }

    internal void Validate(string n)
    {
        ExerciseConfig.Require(MinGapM > 0 && BandMinM > MinGapM && BandMaxM > BandMinM && StartGapM >= BandMinM,
            $"{n}: lead gaps must satisfy 0 < minGapM < bandMinM <= startGapM, bandMinM < bandMaxM");
        ExerciseConfig.Require(FrontAheadOfDriverM >= 0, $"{n}: lead.frontAheadOfDriverM must be >= 0");
        ExerciseConfig.Require(SpeedProfile.Length >= 2 && SpeedProfile.All(p => p.Length == 2 && p[1] >= 0),
            $"{n}: lead.speedProfile must be [time, km/h >= 0] pairs");
        for (int i = 1; i < SpeedProfile.Length; i++)
            ExerciseConfig.Require(SpeedProfile[i][0] > SpeedProfile[i - 1][0], $"{n}: lead.speedProfile times must increase");
        ExerciseConfig.Require(SpeedProfile[^1][1] == 0, $"{n}: lead.speedProfile must end stopped");
    }

    public double EndTimeS => SpeedProfile[^1][0];

    /// <summary>Lead speed (m/s) at a time since the attempt start.</summary>
    public double SpeedAt(double t)
    {
        var p = SpeedProfile;
        if (t <= p[0][0]) return p[0][1] / 3.6;
        for (int i = 1; i < p.Length; i++)
        {
            if (t <= p[i][0])
            {
                double f = (t - p[i - 1][0]) / (p[i][0] - p[i - 1][0]);
                return (p[i - 1][1] + f * (p[i][1] - p[i - 1][1])) / 3.6;
            }
        }
        return 0;
    }
}

/// <summary>The scene's stop line: the car must stop before it, then pull away (M6 chained hill sequence).</summary>
public sealed record StopLineDef
{
    /// <summary>The car's front bumper is this far ahead of the driver's position.</summary>
    public required double FrontAheadOfDriverM { get; init; }
    /// <summary>Ideal gap between the front bumper and the line when stopped.</summary>
    public required double IdealGapM { get; init; }
    /// <summary>The front may cross the line by this much before it counts as running the line.</summary>
    public required double OverrunToleranceM { get; init; }
    /// <summary>Stopped for this long counts as a stop.</summary>
    public required double StopHoldS { get; init; }
    /// <summary>Only a stop with the front within this distance of the line counts.</summary>
    public required double CountsWithinM { get; init; }

    internal void Validate(string n) =>
        ExerciseConfig.Require(FrontAheadOfDriverM >= 0 && IdealGapM >= 0 && OverrunToleranceM >= 0 && StopHoldS > 0 && CountsWithinM > IdealGapM,
            $"{n}: stopLine values must be >= 0 (stopHoldS > 0, countsWithinM > idealGapM)");
}

/// <summary>Drive in <see cref="FromGear"/> at speed, then shift down to <see cref="ToGear"/> (M6 rev matching).</summary>
public sealed record DownshiftDef
{
    public required int FromGear { get; init; }
    public required double MinSpeedKmh { get; init; }
    public required int ToGear { get; init; }
    /// <summary>The lower gear counts as biting once clutch engagement exceeds this.</summary>
    public required double BiteEngagement { get; init; }

    internal void Validate(string n)
    {
        ExerciseConfig.Require(FromGear is >= 2 and <= 6 && ToGear >= 1 && ToGear < FromGear, $"{n}: downshift gears must go down");
        ExerciseConfig.Require(MinSpeedKmh > 0 && BiteEngagement is > 0 and < 1, $"{n}: downshift speed > 0, biteEngagement in (0, 1)");
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
