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
    /// <summary>Centering spring coefficient 0..1 on the hill road; 0 = off. The town map uses the steering force instead.</summary>
    public required double CenteringSpring { get; init; }
    public required double ReconnectIntervalS { get; init; }
    /// <summary>Aligning torque at the steering wheel (physics, M9) that gives full motor force.</summary>
    public required double SteeringFullScaleNm { get; init; }
    /// <summary>Scale on the aligning torque force; 0 = no steering force.</summary>
    public required double SteeringGain { get; init; }
    /// <summary>Force fraction opposing the wheel per degree per second of turning (weight and stability).</summary>
    public required double SteeringDamperPerDegPerS { get; init; }
    /// <summary>Low-pass on the wheel's rate of turn before damping it.</summary>
    public required double SteeringRateFilterHz { get; init; }

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
        Require(SteeringFullScaleNm > 0 && SteeringGain is >= 0 and <= 2 && SteeringDamperPerDegPerS >= 0 && SteeringRateFilterHz > 0,
            "steering: fullScaleNm > 0, gain in [0, 2], damper >= 0, filter > 0");
        Require(JoltLengthMs > 0 && JoltCooldownMs >= 0 && ReconnectIntervalS > 0, "times must be positive");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}
