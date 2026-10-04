using System.Diagnostics;
using Sim.Input;

namespace Sim.App;

/// <summary>What the force-feedback thread reports for display.</summary>
public readonly record struct FfbStatus(bool Connected, string? Device, string? Error, double RateHz,
    long Calls, long Failures, double MaxCallMs, long Jolts);

/// <summary>
/// Force-feedback thread (~100 Hz). Owns the haptic device (an update can take several ms, M0
/// finding H9, so it must not run on the physics thread). Reads the newest physics frame from its
/// own <see cref="LatestValue{T}"/> and maps it to wheel effects; never writes back.
/// </summary>
public sealed class FfbLoop : IDisposable
{
    /// <summary>A jolt fires once the jerk has fallen this far below its peak ...</summary>
    private const double PeakFallRatio = 0.8;
    /// <summary>... or this long after it crossed the threshold, whichever comes first.</summary>
    private const double PeakWaitS = 0.03;

    private readonly LatestValue<Frame> _source;
    private readonly Thread _thread;
    private volatile bool _stop;
    private FfbParams _params;

    public LatestValue<FfbStatus> Status { get; } = new();

    private volatile bool _steeringAxisInverted;

    /// <summary>From the input config: the raw wheel axis decreases as the wheel turns left.</summary>
    public bool SteeringAxisInverted { get => _steeringAxisInverted; set => _steeringAxisInverted = value; }

    public FfbParams Params
    {
        get => Volatile.Read(ref _params);
        set => Volatile.Write(ref _params, value);
    }

    public FfbLoop(LatestValue<Frame> source, FfbParams p)
    {
        _source = source;
        _params = p;
        _thread = new Thread(Run) { Name = "Force feedback", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        ForceFeedback? wheel = null;
        string? error = null;
        var clock = Stopwatch.StartNew();
        double lastS = 0, sinceRetryS = double.MaxValue, rateWindowS = 0, lastJoltS = double.MinValue;
        double filteredAccel = 0, previousFiltered = 0, rateHz = 0, peakJerk = 0, peakStartS = 0;
        double previousWheelDeg = 0, wheelRateDegPerS = 0;
        long ticks = 0, jolts = 0;
        bool primed = false;
        double nextTickS = 0;
        double appliedGain = -1, appliedSpring = -1; // sent only on change; -1 = resend after (re)connect

        try
        {
            while (!_stop)
            {
                var p = Params;
                double now = clock.Elapsed.TotalSeconds;
                double dt = Math.Max(now - lastS, 1e-4);
                lastS = now;
                var f = _source.Read();

                if (wheel == null && f.Input.Connected)
                {
                    sinceRetryS += dt;
                    if (sinceRetryS >= p.ReconnectIntervalS)
                    {
                        sinceRetryS = 0;
                        wheel = ForceFeedback.Open(p.DeviceNameContains, out error);
                        appliedGain = appliedSpring = -1;
                    }
                }
                else if (wheel != null && !f.Input.Connected)
                {
                    wheel.Dispose();
                    wheel = null;
                    error = "wheel disconnected";
                }

                // Jerk from filtered acceleration, so integration noise does not trigger jolts.
                double alpha = 1 - Math.Exp(-2 * Math.PI * p.AccelerationFilterHz * dt);
                filteredAccel += (f.State.AccelerationMps2 - filteredAccel) * alpha;
                double jerk = primed ? (filteredAccel - previousFiltered) / dt : 0;
                previousFiltered = filteredAccel;
                double wheelDeg = f.Input.Input.SteeringWheelDeg;
                double rateAlpha = 1 - Math.Exp(-2 * Math.PI * p.SteeringRateFilterHz * dt);
                wheelRateDegPerS += ((primed ? (wheelDeg - previousWheelDeg) / dt : 0) - wheelRateDegPerS) * rateAlpha;
                previousWheelDeg = wheelDeg;
                primed = true;

                if (wheel != null)
                {
                    var s = f.State;
                    if (p.MasterGain != appliedGain) wheel.SetGain(appliedGain = p.MasterGain);
                    if (p.CenteringSpring != appliedSpring) wheel.SetSpring(appliedSpring = p.CenteringSpring);
                    double firingHz = Math.Max(0, s.EngineRpm) / 60 * s.FiringsPerRev;
                    wheel.SetShudder(firingHz, s.Firing ? s.ShudderIntensity * p.ShudderMagnitude : 0);
                    wheel.SetGrind(p.GrindFrequencyHz, s.Grinding ? p.GrindMagnitude : 0);
                    // Steering (town map): aligning torque, damping and friction, in the sim's sign convention
                    // (positive = towards a left turn), then onto the raw axis direction.
                    double? steer = null;
                    if (s.Steering)
                    {
                        double level = s.AligningTorqueNm / p.SteeringFullScaleNm * p.SteeringGain
                                       - p.SteeringDamperPerDegPerS * wheelRateDegPerS
                                       - p.SteeringFrictionLevel * Math.Tanh(wheelRateDegPerS / p.SteeringFrictionRateDegPerS);
                        steer = Math.Clamp(level, -1, 1) * (SteeringAxisInverted ? -1 : 1);
                    }
                    wheel.SetSteeringForce(steer);
                    // Rough clutch engagement, a stall or a hard stop: a fading shake that grows with the
                    // jerk, so it follows the speed mismatch and how fast the clutch came up. It fires at the
                    // jerk's peak (once it falls back, or after PeakWaitS), not where it crosses the threshold.
                    double absJerk = Math.Abs(jerk);
                    bool ready = (now - lastJoltS) * 1000 >= p.JoltCooldownMs;
                    if (ready && absJerk > p.JoltThresholdMps3)
                    {
                        if (peakJerk == 0) peakStartS = now;
                        peakJerk = Math.Max(peakJerk, absJerk);
                    }
                    if (peakJerk > 0 && (absJerk < peakJerk * PeakFallRatio || now - peakStartS >= PeakWaitS))
                    {
                        double level = Math.Clamp((peakJerk - p.JoltThresholdMps3) / (p.JoltFullScaleMps3 - p.JoltThresholdMps3), 0, 1);
                        wheel.Jolt(level * p.JoltMagnitude, p.JoltFrequencyHz, (uint)p.JoltLengthMs);
                        lastJoltS = now;
                        peakJerk = 0;
                        jolts++;
                    }
                }

                ticks++;
                if (now - rateWindowS >= 1)
                {
                    rateHz = ticks / (now - rateWindowS);
                    ticks = 0;
                    rateWindowS = now;
                }
                Status.Publish(new FfbStatus(wheel != null, wheel?.Name, wheel?.LastError ?? error, rateHz,
                    wheel?.Calls ?? 0, wheel?.Failures ?? 0, wheel?.MaxCallMs ?? 0, jolts));

                // Fixed tick grid, so time spent in SDL calls does not lower the rate.
                nextTickS = Math.Max(nextTickS + 1 / p.UpdateRateHz, now);
                while (!_stop && clock.Elapsed.TotalSeconds < nextTickS) Thread.Sleep(1);
            }
        }
        finally
        {
            wheel?.Dispose();
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
    }
}
