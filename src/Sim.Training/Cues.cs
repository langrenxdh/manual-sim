using System.Text.Json;
using Sim.Core;

namespace Sim.Training;

/// <summary>What a teaching-mode cue reacts to (M8). Each is a condition on the physics state.</summary>
public enum CueCondition
{
    /// <summary>In gear and nearly stopped, the clutch engagement rises past <see cref="CueRule.Threshold"/>.</summary>
    ClutchBiting,
    /// <summary>
    /// The engine is within <see cref="CueRule.Threshold"/> rpm of stalling while the clutch is engaging. (Idle-control
    /// usage is no use here: in the model it saturates even in gentle pull-aways; the stall margin separates them.)
    /// </summary>
    StallMarginLow,
    /// <summary>The car moves against its gear's direction faster than <see cref="CueRule.Threshold"/> m/s.</summary>
    RollingBack,
    /// <summary>Engine speed above the redline start.</summary>
    OverRev,
    /// <summary>The clutch is hot enough to smell.</summary>
    ClutchHot,
}

/// <summary>One cue: when it fires and what is said.</summary>
public sealed record CueRule
{
    public required string Id { get; init; }
    public required CueCondition When { get; init; }
    public double Threshold { get; init; }
    /// <summary>Spoken text (teaching mode only).</summary>
    public required string Say { get; init; }
    /// <summary>Minimum time between two firings of this cue.</summary>
    public required double CooldownS { get; init; }
}

/// <summary>Teaching-mode cues, loaded from <c>config/cues.json</c>.</summary>
public sealed record CueConfig
{
    /// <summary>Below this speed the car counts as nearly stopped (for <see cref="CueCondition.ClutchBiting"/>).</summary>
    public required double NearlyStoppedKmh { get; init; }
    public required CueRule[] Cues { get; init; }

    public static CueConfig FromJson(string json)
    {
        var c = JsonSerializer.Deserialize<CueConfig>(json, ExerciseConfig.JsonOptions)
            ?? throw new JsonException("Empty cues file.");
        ExerciseConfig.Require(c.NearlyStoppedKmh > 0, "nearlyStoppedKmh must be > 0");
        ExerciseConfig.Require(c.Cues.Select(r => r.Id).Distinct().Count() == c.Cues.Length, "cue ids must be unique");
        foreach (var r in c.Cues) ExerciseConfig.Require(r.CooldownS >= 0 && r.Threshold >= 0, $"cue \"{r.Id}\": values must be >= 0");
        return c;
    }
}

/// <summary>
/// Fires a cue when its condition becomes true (an edge, not while it stays true), at most once per
/// cooldown. Pure, so it is tested like the scoring. The host decides how to say it.
/// </summary>
public sealed class CueEngine
{
    private readonly CueConfig _config;
    private readonly VehicleParams _vehicle;
    private readonly bool[] _was;
    private readonly double[] _sinceFired;

    public CueEngine(CueConfig config, VehicleParams vehicle)
    {
        _config = config;
        _vehicle = vehicle;
        _was = new bool[config.Cues.Length];
        _sinceFired = Enumerable.Repeat(double.MaxValue, config.Cues.Length).ToArray();
    }

    /// <summary>Feed one state; returns the cue that fires now, or null.</summary>
    public CueRule? Update(in SimState s, double dtS)
    {
        CueRule? fired = null;
        for (int i = 0; i < _config.Cues.Length; i++)
        {
            var rule = _config.Cues[i];
            _sinceFired[i] += dtS;
            bool now = Holds(rule, s);
            if (now && !_was[i] && _sinceFired[i] >= rule.CooldownS && fired == null)
            {
                fired = rule;
                _sinceFired[i] = 0;
            }
            _was[i] = now;
        }
        return fired;
    }

    private bool Holds(CueRule rule, in SimState s)
    {
        bool inGear = s.EngagedGear != Gear.Neutral;
        double gearDirection = s.EngagedGear == Gear.Reverse ? -1 : 1;
        return rule.When switch
        {
            CueCondition.ClutchBiting => inGear && Math.Abs(s.SpeedKmh) < _config.NearlyStoppedKmh && s.ClutchEngagement > rule.Threshold,
            CueCondition.StallMarginLow => s.Firing && s.ClutchEngagement > 0 && s.StallMarginRpm < rule.Threshold,
            CueCondition.RollingBack => inGear && s.SpeedMps * gearDirection < -rule.Threshold,
            CueCondition.OverRev => s.EngineRpm > _vehicle.Engine.RedlineRpm,
            CueCondition.ClutchHot => s.ClutchSmell,
            _ => false,
        };
    }
}
