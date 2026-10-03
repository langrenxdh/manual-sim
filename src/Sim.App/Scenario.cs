using System.Text.Json;

namespace Sim.App;

/// <summary>
/// Session settings that are not part of any config file. M2 has only a constant road grade, so
/// hill starts can be tried before M3 builds the real hill. Changing it restarts the car.
/// </summary>
public sealed record Scenario
{
    public const double MaxGradePercent = 30;

    public required double GradePercent { get; init; }

    public static Scenario FromJson(string json)
    {
        var s = JsonSerializer.Deserialize<Scenario>(json, Sim.Core.VehicleParams.JsonOptions)
            ?? throw new JsonException("Empty scenario.");
        if (Math.Abs(s.GradePercent) > MaxGradePercent)
            throw new ArgumentException($"gradePercent must be within +-{MaxGradePercent}");
        return s;
    }
}
