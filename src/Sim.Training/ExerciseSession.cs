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
    int Stalls,
    double GapOutsideBandS = 0,
    double StopErrorM = 0,
    double OverSpeedS = 0,
    double BrakeS = 0,
    double CoastingS = 0,
    double RevMatchErrorRpm = 0,
    double DownshiftJerkMps3 = 0,
    double OffRoadS = 0)
{
    public double Get(Metric m) => m switch
    {
        Metric.ClutchSlipEnergyKJ => ClutchSlipEnergyKJ,
        Metric.PeakJerkMps3 => PeakJerkMps3,
        Metric.RollbackM => RollbackM,
        Metric.GrindingS => GrindingS,
        Metric.OverRevS => OverRevS,
        Metric.TimeS => ElapsedS,
        Metric.GapOutsideBandS => GapOutsideBandS,
        Metric.StopErrorM => StopErrorM,
        Metric.OverSpeedS => OverSpeedS,
        Metric.BrakeS => BrakeS,
        Metric.CoastingS => CoastingS,
        Metric.RevMatchErrorRpm => RevMatchErrorRpm,
        Metric.DownshiftJerkMps3 => DownshiftJerkMps3,
        Metric.OffRoadS => OffRoadS,
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };
}

/// <summary>What the exercise UI shows live besides the metrics (lead car, stop line, downshift).</summary>
public readonly record struct AttemptLive(
    double? LeadPositionM,
    bool LeadBraking,
    double? GapM,
    bool StoppedAtLine,
    bool DownshiftArmed,
    bool DownshiftDone,
    bool OffRoad = false,
    int ViaDone = 0);

public enum AttemptPhase { Running, Completed, Failed }

/// <summary>
/// One attempt at an exercise. Fed every physics step; measures the metrics, decides when the
/// attempt is completed or failed, then scores it. Pure: identical in the app and in tests.
/// Exercise rules (finish line, stop line, lead car) are judging, not physics: they never act on the car.
/// </summary>
public sealed class ExerciseSession
{
    private readonly ExerciseConfig _config;
    private readonly double _engineStallRpm, _redlineRpm;
    private readonly double? _stopLineM;
    private readonly double _dir; // +1 forward, -1 reverse
    private double _startPosition, _referencePosition, _elapsed, _slipEnergyJ, _peakJerk, _rollback, _grinding, _overRev, _distance;
    private double _filteredAccel, _previousFiltered, _finishHeld;
    private double _gapOutside, _stopError, _overSpeed, _brake, _coasting, _revMatchError, _downshiftJerk;
    private double _leadPosition, _leadPreviousSpeed, _stoppedFor;
    private double? _gap;
    private readonly TownMap? _town;
    private double _offRoad;
    private bool _offRoadNow;
    private int _viaDone;
    private bool _primed, _wasFiring, _stoppedAtLine, _downshiftArmed, _downshiftDone, _leadBraking;
    private int _stalls;
    private readonly List<TraceSample> _trace;
    private double _sinceSample = double.MaxValue;

    public ExerciseDef Exercise { get; }
    public AttemptPhase Phase { get; private set; } = AttemptPhase.Running;
    /// <summary>Why the attempt failed, or null.</summary>
    public string? FailReason { get; private set; }
    /// <summary>The score once the attempt has ended, otherwise null.</summary>
    public ScoreResult? Result { get; private set; }

    /// <param name="scene">Where the stop line is; needed only by exercises with a <see cref="ExerciseDef.StopLine"/>.</param>
    /// <param name="town">The town map; needed only by exercises with a <see cref="ExerciseDef.TownStart"/>.</param>
    public ExerciseSession(ExerciseConfig config, ExerciseDef exercise, VehicleParams vehicle, Scene? scene = null, TownMap? town = null)
    {
        if (exercise.StopLine != null && scene == null)
            throw new ArgumentException($"Exercise \"{exercise.Id}\" has a stop line and needs the scene.", nameof(scene));
        if (exercise.TownStart != null)
        {
            _town = town ?? throw new ArgumentException($"Exercise \"{exercise.Id}\" runs in town and needs the town map.", nameof(town));
            town.Start(exercise.TownStart); // throws for an unknown start
            FinishZone = exercise.FinishZone?.Resolve(town);
        }
        _config = config;
        Exercise = exercise;
        _engineStallRpm = vehicle.Engine.StallRpm;
        _redlineRpm = vehicle.Engine.RedlineRpm;
        _stopLineM = scene?.StopLineM;
        _dir = exercise.Direction == TravelDirection.Reverse ? -1 : 1;
        // Sized for the whole time limit up front, so the physics thread never reallocates mid-attempt.
        _trace = new List<TraceSample>((int)Math.Ceiling(exercise.TimeLimitS / Trace.IntervalS) + 2);
    }

    /// <summary>
    /// The vehicle parameters this exercise runs with: unchanged except for a forced hill-start
    /// assist setting. Never changes the physics model itself.
    /// </summary>
    public static VehicleParams ApplyTo(ExerciseDef exercise, VehicleParams vehicle) =>
        exercise.HillHold is bool on ? vehicle with { HillHold = vehicle.HillHold with { Enabled = on } } : vehicle;

    public AttemptMetrics Metrics => new(_elapsed, _distance, _slipEnergyJ / 1000, _peakJerk, _rollback, _grinding,
        _overRev, _stalls, _gapOutside, _stopError, _overSpeed, _brake, _coasting, _revMatchError, _downshiftJerk, _offRoad);

    public AttemptLive Live => new(Exercise.Lead != null ? _leadPosition : null, _leadBraking, _gap, _stoppedAtLine,
        _downshiftArmed, _downshiftDone, _offRoadNow, _viaDone);

    /// <summary>A town exercise's finish zone as [minX, minY, maxX, maxY], or null.</summary>
    public double[]? FinishZone { get; }

    /// <summary>Feed one physics step (after <see cref="Simulator.Step"/>) with the input that drove it.</summary>
    public void Observe(in SimState s, in DriverInput input, double dtS)
    {
        if (Phase != AttemptPhase.Running) return;
        var e = Exercise;

        // Trace for the ghost comparison, sampled from the attempt start.
        _sinceSample += dtS;
        if (_sinceSample >= Trace.IntervalS - 1e-9)
        {
            _sinceSample = 0;
            _trace.Add(new TraceSample((float)_elapsed, (float)input.Clutch, (float)input.Throttle, (float)input.Brake,
                (float)s.EngineRpm, (float)s.SpeedKmh));
        }

        if (!_primed)
        {
            _startPosition = _referencePosition = s.PositionM;
            _filteredAccel = _previousFiltered = s.AccelerationMps2;
            _wasFiring = s.Firing;
            if (e.Lead is { } lead) _leadPosition = s.PositionM + lead.FrontAheadOfDriverM + lead.StartGapM;
            _primed = true;
        }

        _elapsed += dtS;
        // Progress and rollback count in the travel direction; after a stop at the line they count from there.
        _distance = (s.PositionM - _referencePosition) * _dir;
        _rollback = Math.Max(_rollback, -_distance);
        double speedKmh = Math.Abs(s.SpeedKmh);
        bool stopped = speedKmh < _config.StoppedBelowKmh;

        // Heat into the clutch: transmitted torque times slip speed.
        double slipRadPerS = s.ClutchSlipRpm * Math.PI / 30;
        _slipEnergyJ += Math.Abs(s.ClutchTorqueNm * slipRadPerS) * dtS;

        double alpha = 1 - Math.Exp(-2 * Math.PI * _config.JerkFilterHz * dtS);
        _filteredAccel += (s.AccelerationMps2 - _filteredAccel) * alpha;
        double jerk = Math.Abs(_filteredAccel - _previousFiltered) / dtS;
        _peakJerk = Math.Max(_peakJerk, jerk);
        _previousFiltered = _filteredAccel;

        if (s.Grinding) _grinding += dtS;
        if (s.EngineRpm > _redlineRpm) _overRev += dtS;
        if (e.SpeedLimitKmh is double limit && speedKmh > limit) _overSpeed += dtS;
        if (input.Brake > _config.PedalPressedAbove) _brake += dtS;
        bool disconnected = s.EngagedGear == Gear.Neutral || s.ClutchEngagement < _config.CoastingMaxEngagement;
        if (speedKmh > _config.CoastingMinSpeedKmh && disconnected) _coasting += dtS;

        // Same rule as the replay's stall marker: combustion stops below the stall threshold.
        if (_wasFiring && !s.Firing && s.EngineRpm < _engineStallRpm) _stalls++;
        _wasFiring = s.Firing;

        string? fail = ObserveLead(e.Lead, s, dtS) ?? ObserveStopLine(e.StopLine, s, stopped, dtS);
        ObserveDownshift(e.Downshift, s, jerk);
        ObserveTown(s, dtS);

        if (_stalls > 0) End(AttemptPhase.Failed, "stalled");
        else if (fail != null) End(AttemptPhase.Failed, fail);
        else if (e.MaxRollbackM is double max && _rollback > max) End(AttemptPhase.Failed, $"rolled back more than {max:0.##} m");
        else if (_elapsed > e.TimeLimitS) End(AttemptPhase.Failed, $"not finished within {e.TimeLimitS:0} s");
        else
        {
            bool atFinish = AtFinish(e, s, speedKmh, stopped);
            _finishHeld = atFinish ? _finishHeld + dtS : 0;
            if (atFinish && _finishHeld >= e.FinishHoldS) End(AttemptPhase.Completed, null);
        }
    }

    private bool AtFinish(ExerciseDef e, in SimState s, double speedKmh, bool stopped)
    {
        // Queue: after the lead's last stop, being stopped behind it finishes; the gap itself is scored.
        if (e.Lead is { } lead)
            return _elapsed >= lead.EndTimeS && stopped;
        if (e.StopLine != null && !_stoppedAtLine) return false;
        if (e.Downshift is { } d && !_downshiftDone) return false;
        if (_town != null && !InFinishZone(s)) return false;
        if (e.FinishZone is { Stopped: true }) return stopped;

        int gear = (int)s.EngagedGear;
        bool inGear = e.Downshift is { } ds ? gear == ds.ToGear
            : e.Direction == TravelDirection.Reverse ? s.EngagedGear == Gear.Reverse
            : gear >= e.FinishGear;
        // In town the distance along the road means nothing (turns, reversing); the zone replaces it.
        bool farEnough = _town != null || _distance >= e.FinishMinDistanceM;
        return inGear && s.ClutchLocked && speedKmh >= e.FinishMinSpeedKmh && farEnough;
    }

    /// <summary>Town exercises: time with the car partly off the road, and progress through the via areas.</summary>
    private void ObserveTown(in SimState s, double dtS)
    {
        if (_town == null) return;
        _offRoadNow = false;
        foreach (var (x, y) in _config.TownCar!.Corners(s.WorldX, s.WorldY, s.HeadingRad))
        {
            if (_town.OnRoad(x, y)) continue;
            _offRoadNow = true;
            break;
        }
        if (_offRoadNow) _offRoad += dtS;
        var via = Exercise.Via;
        if (_viaDone < via.Length && TownZoneDef.Inside(via[_viaDone], s.WorldX, s.WorldY)) _viaDone++;
    }

    /// <summary>Every via area passed, the whole car inside the finish zone and pointing the right way.</summary>
    private bool InFinishZone(in SimState s)
    {
        if (_viaDone < Exercise.Via.Length) return false;
        if (FinishZone is not { } zone || Exercise.FinishZone is not { } z) return true;
        if (z.HeadingDeg is double want)
        {
            double diff = Math.IEEERemainder(s.HeadingRad * 180 / Math.PI - want, 360);
            if (Math.Abs(diff) > z.HeadingToleranceDeg) return false;
        }
        return _config.TownCar!.Corners(s.WorldX, s.WorldY, s.HeadingRad).All(c => TownZoneDef.Inside(zone, c.X, c.Y));
    }

    /// <summary>Moves the scripted lead car and judges the gap. Returns a failure reason or null.</summary>
    private string? ObserveLead(LeadCarDef? lead, in SimState s, double dtS)
    {
        if (lead == null) return null;
        double speed = lead.SpeedAt(_elapsed);
        _leadBraking = speed < _leadPreviousSpeed - 1e-9 || speed == 0;
        _leadPreviousSpeed = speed;
        _leadPosition += speed * dtS;
        double gap = _leadPosition - (s.PositionM + lead.FrontAheadOfDriverM);
        _gap = gap;
        if (gap < lead.MinGapM) return "too close to the car ahead";
        if (gap < lead.BandMinM || gap > lead.BandMaxM) _gapOutside += dtS;
        return null;
    }

    /// <summary>Judges the stop before the line. Returns a failure reason or null.</summary>
    private string? ObserveStopLine(StopLineDef? line, in SimState s, bool stopped, double dtS)
    {
        if (line == null || _stoppedAtLine || _stopLineM is not double lineM) return null;
        double front = s.PositionM + line.FrontAheadOfDriverM;
        if (front > lineM + line.OverrunToleranceM) return "ran the stop line";
        _stoppedFor = stopped ? _stoppedFor + dtS : 0;
        bool nearLine = front >= lineM - line.CountsWithinM; // waiting far back (e.g. at the start) is not "the stop"
        if (_stoppedFor >= line.StopHoldS && nearLine)
        {
            _stoppedAtLine = true;
            _stopError = Math.Abs(front - (lineM - line.IdealGapM));
            _referencePosition = s.PositionM;
            _rollback = 0;
            _distance = 0;
        }
        return null;
    }

    /// <summary>Arms on reaching the higher gear at speed; measures rev matching when the lower gear bites.</summary>
    private void ObserveDownshift(DownshiftDef? d, in SimState s, double jerk)
    {
        if (d == null) return;
        int gear = (int)s.EngagedGear;
        if (!_downshiftArmed && gear == d.FromGear && Math.Abs(s.SpeedKmh) >= d.MinSpeedKmh) _downshiftArmed = true;
        if (_downshiftArmed && !_downshiftDone && gear == d.ToGear && s.ClutchEngagement > d.BiteEngagement)
        {
            _downshiftDone = true;
            _revMatchError = Math.Abs(s.ClutchSlipRpm);
        }
        if (_downshiftDone) _downshiftJerk = Math.Max(_downshiftJerk, jerk);
    }

    private void End(AttemptPhase phase, string? reason)
    {
        Phase = phase;
        FailReason = reason;
        Result = Scorer.Score(_config, Exercise, Metrics, failed: phase == AttemptPhase.Failed, reason, _trace.ToArray());
    }
}
