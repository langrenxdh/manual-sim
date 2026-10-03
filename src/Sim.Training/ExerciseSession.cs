using Sim.Core;

namespace Sim.Training;

/// <summary>Everything measured during one attempt so far.</summary>
public readonly record struct AttemptMetrics(
    double ElapsedS,
    double DistanceM,
    double ClutchSlipEnergyKJ,
    double PeakJerkMps3,
    double RollbackM,
    double GrindingS,
    double OverRevS,
    int Stalls)
{
    public double Get(Metric m) => m switch
    {
        Metric.ClutchSlipEnergyKJ => ClutchSlipEnergyKJ,
        Metric.PeakJerkMps3 => PeakJerkMps3,
        Metric.RollbackM => RollbackM,
        Metric.GrindingS => GrindingS,
        Metric.OverRevS => OverRevS,
        Metric.TimeS => ElapsedS,
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };
}

public enum AttemptPhase { Running, Completed, Failed }

/// <summary>
/// One attempt at an exercise. Fed every physics step; measures the metrics, decides when the
/// attempt is completed or failed, then scores it. Pure: identical in the app and in tests.
/// </summary>
public sealed class ExerciseSession
{
    private readonly ExerciseConfig _config;
    private readonly double _engineStallRpm, _redlineRpm;
    private double _startPosition, _elapsed, _slipEnergyJ, _peakJerk, _rollback, _grinding, _overRev, _distance;
    private double _filteredAccel, _previousFiltered, _finishHeld;
    private bool _primed, _wasFiring;
    private int _stalls;

    public ExerciseDef Exercise { get; }
    public AttemptPhase Phase { get; private set; } = AttemptPhase.Running;
    /// <summary>Why the attempt failed, or null.</summary>
    public string? FailReason { get; private set; }
    /// <summary>The score once the attempt has ended, otherwise null.</summary>
    public ScoreResult? Result { get; private set; }

    public ExerciseSession(ExerciseConfig config, ExerciseDef exercise, VehicleParams vehicle)
    {
        _config = config;
        Exercise = exercise;
        _engineStallRpm = vehicle.Engine.StallRpm;
        _redlineRpm = vehicle.Engine.RedlineRpm;
    }

    /// <summary>
    /// The vehicle parameters this exercise runs with: unchanged except for a forced hill-start
    /// assist setting. Never changes the physics model itself.
    /// </summary>
    public static VehicleParams ApplyTo(ExerciseDef exercise, VehicleParams vehicle) =>
        exercise.HillHold is bool on ? vehicle with { HillHold = vehicle.HillHold with { Enabled = on } } : vehicle;

    public AttemptMetrics Metrics => new(_elapsed, _distance, _slipEnergyJ / 1000, _peakJerk, _rollback, _grinding,
        _overRev, _stalls);

    /// <summary>Feed one physics step (after <see cref="Simulator.Step"/>).</summary>
    public void Observe(in SimState s, double dtS)
    {
        if (Phase != AttemptPhase.Running) return;

        if (!_primed)
        {
            _startPosition = s.PositionM;
            _filteredAccel = _previousFiltered = s.AccelerationMps2;
            _wasFiring = s.Firing;
            _primed = true;
        }

        _elapsed += dtS;
        _distance = s.PositionM - _startPosition;
        _rollback = Math.Max(_rollback, -_distance);

        // Heat into the clutch: transmitted torque times slip speed.
        double slipRadPerS = s.ClutchSlipRpm * Math.PI / 30;
        _slipEnergyJ += Math.Abs(s.ClutchTorqueNm * slipRadPerS) * dtS;

        double alpha = 1 - Math.Exp(-2 * Math.PI * _config.JerkFilterHz * dtS);
        _filteredAccel += (s.AccelerationMps2 - _filteredAccel) * alpha;
        _peakJerk = Math.Max(_peakJerk, Math.Abs(_filteredAccel - _previousFiltered) / dtS);
        _previousFiltered = _filteredAccel;

        if (s.Grinding) _grinding += dtS;
        if (s.EngineRpm > _redlineRpm) _overRev += dtS;

        // Same rule as the replay's stall marker: combustion stops below the stall threshold.
        if (_wasFiring && !s.Firing && s.EngineRpm < _engineStallRpm) _stalls++;
        _wasFiring = s.Firing;

        var e = Exercise;
        if (_stalls > 0) End(AttemptPhase.Failed, "stalled");
        else if (e.MaxRollbackM is double max && _rollback > max) End(AttemptPhase.Failed, $"rolled back more than {max:0.##} m");
        else if (_elapsed > e.TimeLimitS) End(AttemptPhase.Failed, $"not finished within {e.TimeLimitS:0} s");
        else
        {
            bool atFinish = (int)s.EngagedGear == e.FinishGear && s.ClutchLocked
                            && s.SpeedKmh >= e.FinishMinSpeedKmh && _distance >= e.FinishMinDistanceM;
            _finishHeld = atFinish ? _finishHeld + dtS : 0;
            if (atFinish && _finishHeld >= e.FinishHoldS) End(AttemptPhase.Completed, null);
        }
    }

    private void End(AttemptPhase phase, string? reason)
    {
        Phase = phase;
        FailReason = reason;
        Result = Scorer.Score(_config, Exercise, Metrics, failed: phase == AttemptPhase.Failed, reason);
    }
}
