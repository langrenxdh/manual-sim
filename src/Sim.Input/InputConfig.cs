using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sim.Input;

/// <summary>
/// Wheel/pedal mapping and pedal conditioning, loaded from <c>config/g29.json</c>.
/// The caller reads the file and passes the JSON text to <see cref="FromJson"/>.
/// </summary>
public sealed record InputConfig
{
    /// <summary>The first joystick whose name contains this text is used.</summary>
    public required string DeviceNameContains { get; init; }
    public required PedalConfig Clutch { get; init; }
    public required PedalConfig Throttle { get; init; }
    public required PedalConfig Brake { get; init; }
    /// <summary>Button numbers of gears 1 to 6 on the H-shifter.</summary>
    public required int[] ForwardGearButtons { get; init; }
    public required int ReverseGearButton { get; init; }
    /// <summary>Held: starter motor cranks.</summary>
    public required int StarterButton { get; init; }
    /// <summary>Pressed: handbrake toggles on/off.</summary>
    public required int HandbrakeButton { get; init; }
    /// <summary>Pressed: switch between immersion and teaching mode.</summary>
    public required int TeachingModeButton { get; init; }
    /// <summary>Pressed: show or hide the replay of the last seconds.</summary>
    public required int ReplayButton { get; init; }
    /// <summary>Pressed: put the car on the hill just before the stop line.</summary>
    public required int HillStartButton { get; init; }
    /// <summary>How often to look for the wheel again while it is disconnected.</summary>
    public required double ReconnectIntervalS { get; init; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static InputConfig FromJson(string json)
    {
        var c = JsonSerializer.Deserialize<InputConfig>(json, JsonOptions)
            ?? throw new JsonException("Empty input config file.");
        c.Validate();
        return c;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Throws <see cref="ArgumentException"/> if a value is meaningless.</summary>
    public void Validate()
    {
        Clutch.Validate("clutch");
        Throttle.Validate("throttle");
        Brake.Validate("brake");
        Require(ForwardGearButtons.Length == 6, "forwardGearButtons must list 6 gears");
        Require(ForwardGearButtons.Append(ReverseGearButton).Append(StarterButton).Append(HandbrakeButton)
            .Append(TeachingModeButton).Append(ReplayButton).Append(HillStartButton)
            .All(b => b >= 0), "button numbers must be >= 0");
        Require(ReconnectIntervalS > 0, "reconnectIntervalS must be > 0");
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}

public sealed record PedalConfig
{
    public required int Axis { get; init; }
    /// <summary>True when the raw axis value decreases as the pedal is pressed.</summary>
    public required bool Inverted { get; init; }
    /// <summary>Travel at the released end that still reads as 0 (fraction of full travel).</summary>
    public required double DeadZoneReleased { get; init; }
    /// <summary>Travel at the floored end that already reads as 1.</summary>
    public required double DeadZonePressed { get; init; }
    /// <summary>First-order low-pass that smooths the 8-bit quantisation steps.</summary>
    public required double LowPassCutoffHz { get; init; }

    internal void Validate(string name)
    {
        InputConfig.Require(Axis >= 0, $"{name}.axis must be >= 0");
        InputConfig.Require(DeadZoneReleased >= 0 && DeadZonePressed >= 0 && DeadZoneReleased + DeadZonePressed < 1,
            $"{name} dead zones must be >= 0 and leave some travel");
        InputConfig.Require(LowPassCutoffHz > 0, $"{name}.lowPassCutoffHz must be > 0");
    }
}
