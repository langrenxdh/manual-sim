using System.Text.Json;
using Sim.Core;

namespace Sim.App;

/// <summary>
/// Machine-specific settings from first-time setup (<c>config/setup.json</c>, not in git; defaults in
/// <c>config/setup.example.json</c>): screen and seat geometry for the field of view, and the
/// speakers' low-frequency limit for the engine sound.
/// </summary>
public sealed record SetupParams
{
    /// <summary>Visible width of the screen area the window covers when fullscreen.</summary>
    public required double ScreenWidthCm { get; init; }
    /// <summary>Distance from the eyes to the screen.</summary>
    public required double ViewingDistanceCm { get; init; }
    /// <summary>Lowest frequency the speakers audibly play (speaker sweep).</summary>
    public required double SpeakerLowCutHz { get; init; }

    public static SetupParams FromJson(string json)
    {
        var p = JsonSerializer.Deserialize<SetupParams>(json, VehicleParams.JsonOptions)
            ?? throw new JsonException("Empty setup file.");
        p.Validate();
        return p;
    }

    public void Validate()
    {
        Require(ScreenWidthCm is >= 10 and <= 500, "screenWidthCm must be in [10, 500]");
        Require(ViewingDistanceCm is >= 20 and <= 500, "viewingDistanceCm must be in [20, 500]");
        Require(SpeakerLowCutHz is >= 0 and <= 500, "speakerLowCutHz must be in [0, 500]");
    }

    /// <summary>
    /// Vertical field of view that makes the road view a true window onto the road: its physical
    /// height on the screen, seen from the viewing distance.
    /// </summary>
    public double VerticalFovDeg(float viewHeightPx, int monitorWidthPx)
    {
        double cmPerPx = ScreenWidthCm / Math.Max(1, monitorWidthPx);
        double halfHeightCm = viewHeightPx * cmPerPx / 2;
        return 2 * Math.Atan(halfHeightCm / ViewingDistanceCm) * 180 / Math.PI;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}
