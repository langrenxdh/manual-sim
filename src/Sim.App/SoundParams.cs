using System.Text.Json;
using Sim.Core;

namespace Sim.App;

/// <summary>
/// Engine sound synthesis settings for one car, loaded from the sound file <c>cars.json</c> names for it
/// (<c>config/engine-sound.json</c> for the Golf).
/// </summary>
public sealed record SoundParams
{
    public required double MasterGain { get; init; }
    /// <summary>[order, gain] pairs; order is a multiple of the firing frequency.</summary>
    public required double[][] Harmonics { get; init; }
    /// <summary>Combustion torque that counts as full load.</summary>
    public required double LoadReferenceNm { get; init; }
    /// <summary>Loudness at zero load; rises by <see cref="LoadLoudness"/> at full load.</summary>
    public required double IdleLoudness { get; init; }
    public required double LoadLoudness { get; init; }
    /// <summary>How much higher harmonics grow with load (0 = timbre independent of load).</summary>
    public required double LoadBrightness { get; init; }
    /// <summary>Depth of the irregular per-firing amplitude modulation at full shudder (lugging).</summary>
    public required double LugModulationDepth { get; init; }
    /// <summary>Harmonics of this order and above are modulated when lugging.</summary>
    public required double LugMinOrder { get; init; }
    /// <summary>Gain while the engine turns without combustion (fuel cut, spinning down).</summary>
    public required double NotFiringGain { get; init; }
    /// <summary>Sound fades in linearly from 0 rpm up to this speed.</summary>
    public required double AudibleFromRpm { get; init; }
    public required double TurboGain { get; init; }
    public required double TurboCutoffHz { get; init; }
    public required double StarterGain { get; init; }
    public required double StarterFrequencyHz { get; init; }
    /// <summary>Time constant for gain changes, so they do not click.</summary>
    public required double ParameterSmoothingS { get; init; }
    /// <summary>Gain kept by harmonics below the speaker's low-frequency limit (from first-time setup).</summary>
    public required double BelowSpeakerGain { get; init; }
    /// <summary>Fraction of the removed gain added to the next two harmonics above the limit.</summary>
    public required double LowCutCompensation { get; init; }
    /// <summary>Exhaust rasp: noise pulsed by each firing, growing with load (M12).</summary>
    public required double ExhaustRaspGain { get; init; }
    public required double ExhaustRaspCutoffHz { get; init; }
    /// <summary>Tyre squeal while the driven tyres slide (M12), at full strength from <see cref="SquealFullSlipMps"/> of slip.</summary>
    public required double SquealGain { get; init; }
    public required double SquealHz { get; init; }
    public required double SquealFullSlipMps { get; init; }

    public static SoundParams FromJson(string json)
    {
        var p = JsonSerializer.Deserialize<SoundParams>(json, VehicleParams.JsonOptions)
            ?? throw new JsonException("Empty engine sound file.");
        p.Validate();
        return p;
    }

    public void Validate()
    {
        Require(MasterGain >= 0, "masterGain must be >= 0");
        Require(Harmonics.Length > 0 && Harmonics.All(h => h.Length == 2 && h[0] > 0 && h[1] >= 0),
            "harmonics must be [order > 0, gain >= 0] pairs");
        Require(LoadReferenceNm > 0, "loadReferenceNm must be > 0");
        Require(IdleLoudness >= 0 && LoadLoudness >= 0 && LoadBrightness >= 0, "loudness and brightness must be >= 0");
        Require(LugModulationDepth is >= 0 and <= 1, "lugModulationDepth must be in [0, 1]");
        Require(NotFiringGain >= 0, "notFiringGain must be >= 0");
        Require(AudibleFromRpm > 0, "audibleFromRpm must be > 0");
        Require(TurboGain >= 0 && TurboCutoffHz > 0, "turbo gain must be >= 0 and cutoff > 0");
        Require(StarterGain >= 0 && StarterFrequencyHz > 0, "starter gain must be >= 0 and frequency > 0");
        Require(ParameterSmoothingS > 0, "parameterSmoothingS must be > 0");
        Require(BelowSpeakerGain is >= 0 and <= 1, "belowSpeakerGain must be in [0, 1]");
        Require(LowCutCompensation >= 0, "lowCutCompensation must be >= 0");
        Require(ExhaustRaspGain >= 0 && ExhaustRaspCutoffHz > 0, "exhaust rasp gain must be >= 0 and cutoff > 0");
        Require(SquealGain >= 0 && SquealHz > 0 && SquealFullSlipMps > 0, "squeal gain must be >= 0, frequency and full slip > 0");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}
