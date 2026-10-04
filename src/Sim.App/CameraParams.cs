using System.Text.Json;
using Sim.Core;

namespace Sim.App;

/// <summary>Driver's-eye camera settings, loaded from <c>config/camera.json</c>. Presentation only.</summary>
public sealed record CameraParams
{
    public required double EyeHeightM { get; init; }
    /// <summary>Seat offset to the right of the lane centre (right-hand drive).</summary>
    public required double DriverLateralOffsetM { get; init; }
    /// <summary>Used until first-time setup has measured the real screen and viewing distance.</summary>
    public required double DefaultVerticalFovDeg { get; init; }
    /// <summary>Steady body pitch per g of longitudinal acceleration (nose up when accelerating).</summary>
    public required double BodyPitchDegPerG { get; init; }
    public required double BodyPitchFrequencyHz { get; init; }
    /// <summary>Below 1 the body nods past its target before settling (clutch dump).</summary>
    public required double BodyPitchDampingRatio { get; init; }
    /// <summary>Random pitch jitter at full judder intensity.</summary>
    public required double ShakePitchDeg { get; init; }
    public required double ShakeHeightM { get; init; }
    public required double DrawDistanceM { get; init; }
    /// <summary>
    /// The eyes rest this far below the horizon, as a driver's do. With a true-to-scale field of view
    /// (first-time setup) this is what brings the bonnet and the road just ahead of it into view.
    /// </summary>
    public required double LookDownDeg { get; init; }
    /// <summary>The car's own body seen from the seat (bonnet, dashboard, A-pillars).</summary>
    public required CockpitParams Cockpit { get; init; }
    /// <summary>Interior and wing mirrors, each drawn from its own camera.</summary>
    public required MirrorDef[] Mirrors { get; init; }
    /// <summary>The top-down view (town map), toggled with B.</summary>
    public required OverheadParams Overhead { get; init; }

    public static CameraParams FromJson(string json)
    {
        var p = JsonSerializer.Deserialize<CameraParams>(json, VehicleParams.JsonOptions)
            ?? throw new JsonException("Empty camera file.");
        p.Validate();
        return p;
    }

    public void Validate()
    {
        Require(EyeHeightM > 0, "eyeHeightM must be > 0");
        Require(DefaultVerticalFovDeg is > 5 and < 120, "defaultVerticalFovDeg must be in (5, 120)");
        Require(BodyPitchFrequencyHz > 0 && BodyPitchDampingRatio > 0, "body pitch frequency and damping must be > 0");
        Require(ShakePitchDeg >= 0 && ShakeHeightM >= 0, "shake amplitudes must be >= 0");
        Require(DrawDistanceM >= 100, "drawDistanceM must be >= 100");
        Require(LookDownDeg is >= 0 and < 30, "lookDownDeg must be in [0, 30)");
        var c = Cockpit;
        Require(c.CowlAheadM > c.DashAheadM && c.DashAheadM > 0 && c.PillarTopAheadM >= 0 && c.PillarTopAheadM < c.CowlAheadM,
            "cockpit: 0 < dashAheadM < cowlAheadM and 0 <= pillarTopAheadM < cowlAheadM");
        Require(new[] { c.CowlHeightM, c.NoseHeightM, c.DashHeightM, c.PillarTopHeightM, c.PillarWidthM, c.BonnetInsetM }.All(v => v >= 0),
            "cockpit heights and widths must be >= 0");
        foreach (var m in Mirrors)
            Require(m.FovDeg is > 1 and < 120 && m.WidthShare is > 0 and <= 1 && m.Aspect > 0
                    && m.ScreenX is >= 0 and <= 1 && m.ScreenY is >= 0 and <= 1 && m.HeightM > 0,
                "mirrors: fovDeg in (1, 120), widthShare in (0, 1], aspect > 0, screenX/Y in [0, 1], heightM > 0");
        Require(Overhead.SpanM > 0 && Overhead.SizeShare is > 0 and <= 1 && Overhead.ScreenX is >= 0 and <= 1 && Overhead.ScreenY is >= 0 and <= 1,
            "overhead: spanM > 0, sizeShare in (0, 1], screenX/Y in [0, 1]");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}

/// <summary>
/// The own car's body around the driver's eye, in metres. "Ahead" is forward of the eye (which sits over the
/// car's reference point); heights are above the ground; the body's width and nose come from the car outline.
/// </summary>
public sealed record CockpitParams
{
    public required bool Enabled { get; init; }
    /// <summary>Base of the windscreen (where the bonnet starts).</summary>
    public required double CowlAheadM { get; init; }
    public required double CowlHeightM { get; init; }
    /// <summary>Height of the bonnet's front edge, at the car's nose.</summary>
    public required double NoseHeightM { get; init; }
    /// <summary>The bonnet's edges sit this far inside the body's width.</summary>
    public required double BonnetInsetM { get; init; }
    /// <summary>The dashboard top's near edge and height.</summary>
    public required double DashAheadM { get; init; }
    public required double DashHeightM { get; init; }
    public required double PillarTopAheadM { get; init; }
    public required double PillarTopHeightM { get; init; }
    public required double PillarWidthM { get; init; }
}

/// <summary>
/// One mirror: where it sits on the car (ahead of the eye, lateral from the car's centre line, positive to
/// the right, height above the ground), how far it turns outwards from straight back, its field of view, and
/// where it is drawn (centre as a share of the road view, width as a share of the road view's width).
/// </summary>
public sealed record MirrorDef
{
    public required string Name { get; init; }
    public required double AheadM { get; init; }
    public required double LateralM { get; init; }
    public required double HeightM { get; init; }
    /// <summary>Turn away from straight back towards the mirror's own side (0 for the interior mirror).</summary>
    public required double YawOutDeg { get; init; }
    /// <summary>Vertical field of view.</summary>
    public required double FovDeg { get; init; }
    public required double ScreenX { get; init; }
    public required double ScreenY { get; init; }
    public required double WidthShare { get; init; }
    /// <summary>Width / height.</summary>
    public required double Aspect { get; init; }
    /// <summary>Show the car's own body in this mirror (wing mirrors see the car's flank).</summary>
    public required bool ShowsOwnCar { get; init; }
}

/// <summary>
/// The top-down inset: metres of ground across it, its size as a share of the road view's height and its
/// centre as shares of the road view.
/// </summary>
public sealed record OverheadParams
{
    public required double SpanM { get; init; }
    public required double SizeShare { get; init; }
    public required double ScreenX { get; init; }
    public required double ScreenY { get; init; }
}
