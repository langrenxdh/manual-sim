using System.Text.Json;
using Sim.Core;

namespace Sim.App;

/// <summary>
/// The v1 scene, loaded from <c>config/scene.json</c>: flat road, one uphill section with a stop
/// line, flat plateau. Builds the physics <see cref="Road"/>; the 3D view draws the same geometry.
/// </summary>
public sealed record Scene
{
    public const double MaxGradePercent = 20;

    public required HillParams Hill { get; init; }
    /// <summary>Hill-start position: this far before the stop line.</summary>
    public required double HillStartBehindLineM { get; init; }
    public required DashboardParams Dashboard { get; init; }

    public double StopLineM => Hill.StartM + Hill.StopLineFromHillStartM;
    public double HillStartPositionM => StopLineM - HillStartBehindLineM;

    /// <summary>Grade (rise/run) along the road: 0, ramps up, holds, ramps down, 0.</summary>
    public Curve GradeCurve()
    {
        var h = Hill;
        double g = h.GradePercent / 100;
        return new Curve(
        [
            (h.StartM, 0),
            (h.StartM + h.TransitionM, g),
            (h.StartM + h.LengthM - h.TransitionM, g),
            (h.StartM + h.LengthM, 0),
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
        Require(Dashboard.DownshiftRpm > 0 && Dashboard.UpshiftRpm > Dashboard.DownshiftRpm,
            "dashboard.upshiftRpm must be above dashboard.downshiftRpm");
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

/// <summary>Shift suggestion thresholds shown on the instrument strip.</summary>
public sealed record DashboardParams
{
    public required double UpshiftRpm { get; init; }
    public required double DownshiftRpm { get; init; }
}
