using System.Text.Json;
using Sim.Core;

namespace Sim.App;

/// <summary>Force-feedback mapping, loaded from <c>config/ffb.json</c>. Presentation only.</summary>
public sealed record FfbParams
{
    public required string DeviceNameContains { get; init; }
    public required double UpdateRateHz { get; init; }
    public required double MasterGain { get; init; }
    /// <summary>Judder sine magnitude at full judder intensity.</summary>
    public required double ShudderMagnitude { get; init; }
    public required double GrindFrequencyHz { get; init; }
    public required double GrindMagnitude { get; init; }
    /// <summary>Low-pass on longitudinal acceleration before differentiating it into jerk.</summary>
    public required double AccelerationFilterHz { get; init; }
    /// <summary>Jerk below this gives no jolt.</summary>
    public required double JoltThresholdMps3 { get; init; }
    /// <summary>Jerk at which the jolt reaches <see cref="JoltMagnitude"/>.</summary>
    public required double JoltFullScaleMps3 { get; init; }
    public required double JoltMagnitude { get; init; }
    public required double JoltLengthMs { get; init; }
    /// <summary>Minimum time between two jolts.</summary>
    public required double JoltCooldownMs { get; init; }
    /// <summary>Centering spring coefficient 0..1; 0 = off (the wheel does not steer in v1).</summary>
    public required double CenteringSpring { get; init; }
    public required double ReconnectIntervalS { get; init; }

    public static FfbParams FromJson(string json)
    {
        var p = JsonSerializer.Deserialize<FfbParams>(json, VehicleParams.JsonOptions)
            ?? throw new JsonException("Empty force-feedback file.");
        p.Validate();
        return p;
    }

    public void Validate()
    {
        Require(UpdateRateHz is >= 10 and <= 1000, "updateRateHz must be in [10, 1000]");
        Require(new[] { MasterGain, ShudderMagnitude, GrindMagnitude, JoltMagnitude, CenteringSpring }.All(v => v is >= 0 and <= 1),
            "gains and magnitudes must be in [0, 1]");
        Require(GrindFrequencyHz > 0 && AccelerationFilterHz > 0, "frequencies must be > 0");
        Require(JoltThresholdMps3 >= 0 && JoltFullScaleMps3 > JoltThresholdMps3, "joltFullScaleMps3 must be above joltThresholdMps3");
        Require(JoltLengthMs > 0 && JoltCooldownMs >= 0 && ReconnectIntervalS > 0, "times must be positive");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}
