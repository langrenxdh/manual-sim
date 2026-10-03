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
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}
