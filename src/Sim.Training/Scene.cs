using System.Text.Json;
using Sim.Core;

namespace Sim.Training;

/// <summary>
/// The scene, loaded from <c>config/scene.json</c>: flat road, an uphill section with a stop line, a
/// flat plateau, a downhill section, then flat again. Builds the physics <see cref="Road"/>; the 3D
/// view draws the same geometry and the exercises use its positions. Distances in metres along the road.
/// </summary>
public sealed record Scene
{
    public const double MaxGradePercent = 20;

    public required HillParams Hill { get; init; }
    /// <summary>Hill-start position: this far before the stop line.</summary>
    public required double HillStartBehindLineM { get; init; }
    public required DownhillParams Downhill { get; init; }
    public required DashboardParams Dashboard { get; init; }
    /// <summary>Engine temperature on every (re)start (90 = warm). Set it low to practise cold starts (M7).</summary>
    public required double StartEngineTempC { get; init; }

    public double StopLineM => Hill.StartM + Hill.StopLineFromHillStartM;
    public double HillStartPositionM => StopLineM - HillStartBehindLineM;
    public double PlateauStartM => Hill.StartM + Hill.LengthM;
    public double DownhillStartM => PlateauStartM + Downhill.PlateauM;
    public double DownhillEndM => DownhillStartM + Downhill.LengthM;

    /// <summary>Where each exercise start point is on this road.</summary>
    public double PositionOf(StartPoint start) => start switch
    {
        StartPoint.RoadStart => 0,
        StartPoint.HillStart => HillStartPositionM,
        StartPoint.PlateauStart => PlateauStartM + Downhill.StartAfterHillM,
        StartPoint.DownhillBottom => DownhillEndM + Downhill.BottomStartAfterM,
        _ => throw new ArgumentOutOfRangeException(nameof(start)),
    };

    /// <summary>Grade (rise/run) along the road; transitions are linear so the road never kinks.</summary>
    public Curve GradeCurve()
    {
        var h = Hill;
        var d = Downhill;
        double up = h.GradePercent / 100, down = -d.GradePercent / 100;
        return new Curve(
        [
            (h.StartM, 0),
            (h.StartM + h.TransitionM, up),
            (h.StartM + h.LengthM - h.TransitionM, up),
            (h.StartM + h.LengthM, 0),
            (DownhillStartM, 0),
            (DownhillStartM + d.TransitionM, down),
            (DownhillEndM - d.TransitionM, down),
            (DownhillEndM, 0),
        ]);
    }

    public Road BuildRoad() => new(GradeCurve());

    public static Scene FromJson(string json)
    {
        var s = JsonSerializer.Deserialize<Scene>(json, VehicleParams.JsonOptions)
            ?? throw new JsonException("Empty scene file.");
        s.Validate();
        return s;
    }

    public void Validate()
    {
        var h = Hill;
        Require(h.StartM > 0, "hill.startM must be > 0");
        Require(h.TransitionM > 0 && 2 * h.TransitionM < h.LengthM, "hill.transitionM must be > 0 and leave a constant-grade part");
        Require(h.GradePercent is >= 0 and <= MaxGradePercent, $"hill.gradePercent must be in [0, {MaxGradePercent}]");
        Require(h.StopLineFromHillStartM > h.TransitionM && h.StopLineFromHillStartM < h.LengthM - h.TransitionM,
            "hill.stopLineFromHillStartM must lie on the constant-grade part");
        Require(HillStartBehindLineM >= 0 && HillStartBehindLineM < h.StopLineFromHillStartM - h.TransitionM,
            "hillStartBehindLineM must keep the car on the constant-grade part");
        var d = Downhill;
        Require(d.PlateauM > d.StartAfterHillM && d.StartAfterHillM >= 0, "downhill.plateauM must exceed downhill.startAfterHillM >= 0");
        Require(d.TransitionM > 0 && 2 * d.TransitionM < d.LengthM, "downhill.transitionM must be > 0 and leave a constant-grade part");
        Require(d.GradePercent is >= 0 and <= MaxGradePercent, $"downhill.gradePercent must be in [0, {MaxGradePercent}]");
        Require(d.BottomStartAfterM >= 0, "downhill.bottomStartAfterM must be >= 0");
        Require(Dashboard.DownshiftRpm > 0 && Dashboard.UpshiftRpm > Dashboard.DownshiftRpm,
            "dashboard.upshiftRpm must be above dashboard.downshiftRpm");
        Require(StartEngineTempC is >= -30 and <= 120, "startEngineTempC must be within [-30, 120]");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}

public sealed record HillParams
{
    public required double StartM { get; init; }
    public required double LengthM { get; init; }
    public required double GradePercent { get; init; }
    /// <summary>Length over which the grade changes linearly at each end of the hill.</summary>
    public required double TransitionM { get; init; }
    public required double StopLineFromHillStartM { get; init; }
}

/// <summary>The plateau after the hill and the downhill section after it (M6).</summary>
public sealed record DownhillParams
{
    /// <summary>Flat stretch between the top of the hill and the start of the downhill.</summary>
    public required double PlateauM { get; init; }
    public required double LengthM { get; init; }
    /// <summary>Steepness of the descent (positive number; the road falls).</summary>
    public required double GradePercent { get; init; }
    public required double TransitionM { get; init; }
    /// <summary>The downhill exercise starts this far onto the plateau.</summary>
    public required double StartAfterHillM { get; init; }
    /// <summary>The reverse-uphill exercise starts this far past the bottom of the downhill.</summary>
    public required double BottomStartAfterM { get; init; }
}

/// <summary>Shift suggestion thresholds shown on the instrument strip.</summary>
public sealed record DashboardParams
{
    public required double UpshiftRpm { get; init; }
    public required double DownshiftRpm { get; init; }
}
