using System.Diagnostics;
using System.Runtime.InteropServices;
using Sim.Core;
using Sim.Input;

namespace Sim.App;

/// <summary>Everything a consumer needs from one physics step.</summary>
public readonly record struct Frame(
    SimState State,
    InputSample Input,
    string? DeviceName,
    string? InputError,
    double PhysicsHz,
    long Overruns);

/// <summary>
/// The 1 kHz physics thread. It owns the wheel reader and the simulator, steps on wall-clock time
/// and publishes each step to every consumer's <see cref="LatestValue{T}"/>. It never waits for
/// rendering or audio (docs/engineering-rules.en.md hard rule 6). Changes from the UI arrive through atomic
/// references and are applied between steps.
/// </summary>
public sealed class PhysicsLoop : IDisposable
{
    /// <summary>When further behind than this, skip ahead instead of stepping in a burst.</summary>
    private const int MaxCatchUpSteps = 50;
    private const double RateWindowS = 1.0;

    private readonly Thread _thread;
    private volatile bool _stop;
    private VehicleParams _params;
    private InputConfig _inputConfig;
    private VehicleParams? _pendingParams;
    private InputConfig? _pendingInputConfig;
    private ResetRequest? _pendingReset;

    private sealed record ResetRequest(double Grade);

    public LatestValue<Frame> ForRender { get; } = new();
    public LatestValue<Frame> ForAudio { get; } = new();

    public PhysicsLoop(VehicleParams parameters, InputConfig inputConfig)
    {
        _params = parameters;
        _inputConfig = inputConfig;
        _thread = new Thread(Run) { Name = "Physics 1 kHz", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    /// <summary>Applies new vehicle parameters at the next step (already validated by the caller).</summary>
    public void SubmitParams(VehicleParams p) => Volatile.Write(ref _pendingParams, p);

    public void SubmitInputConfig(InputConfig c) => Volatile.Write(ref _pendingInputConfig, c);

    /// <summary>Restarts the car at rest, in neutral, at idle, on a road of constant grade.</summary>
    public void Reset(double grade) => Volatile.Write(ref _pendingReset, new ResetRequest(grade));

    private void Run()
    {
        TimeBeginPeriod(1);
        G29Reader? reader = null;
        string? inputError = null;
        try
        {
            try { reader = new G29Reader(_inputConfig); }
            catch (Exception ex) { inputError = ex.Message; }

            var sim = new Simulator(_params, Road.Flat());
            var clock = Stopwatch.StartNew();
            long stepsDone = 0, overruns = 0, stepsInWindow = 0;
            double windowStart = 0, physicsHz = 0;
            const double stepMs = Simulator.StepS * 1000;

            while (!_stop)
            {
                double now = clock.Elapsed.TotalMilliseconds;
                long due = (long)(now / stepMs) - stepsDone;
                if (due <= 0)
                {
                    Wait(clock, (stepsDone + 1) * stepMs);
                    continue;
                }
                if (due > MaxCatchUpSteps)
                {
                    overruns++;
                    stepsDone += due - 1;
                    due = 1;
                }

                for (long i = 0; i < due; i++)
                {
                    sim = ApplyPending(sim, reader);
                    var sample = reader?.Poll(Simulator.StepS)
                        ?? new InputSample(new DriverInput(0, 0, 0, Gear.Neutral), false, 0, 0, 0, false);
                    sim.Step(sample.Input);
                    stepsDone++;
                    stepsInWindow++;

                    var frame = new Frame(sim.State, sample, reader?.DeviceName, inputError, physicsHz, overruns);
                    ForRender.Publish(frame);
                    ForAudio.Publish(frame);
                }

                if (now - windowStart >= RateWindowS * 1000)
                {
                    physicsHz = stepsInWindow / ((now - windowStart) / 1000);
                    stepsInWindow = 0;
                    windowStart = now;
                }
            }
        }
        finally
        {
            reader?.Dispose();
            TimeEndPeriod(1);
        }
    }

    private Simulator ApplyPending(Simulator sim, G29Reader? reader)
    {
        if (Interlocked.Exchange(ref _pendingParams, null) is { } p)
        {
            _params = p;
            sim.Params = p;
        }
        if (Interlocked.Exchange(ref _pendingInputConfig, null) is { } c)
        {
            _inputConfig = c;
            if (reader != null) reader.Config = c;
        }
        return Interlocked.Exchange(ref _pendingReset, null) is { } r
            ? new Simulator(_params, Road.Constant(r.Grade))
            : sim;
    }

    /// <summary>Sleeps while far from the deadline, then spins: Sleep(1) alone overshoots.</summary>
    private static void Wait(Stopwatch clock, double deadlineMs)
    {
        while (true)
        {
            double remaining = deadlineMs - clock.Elapsed.TotalMilliseconds;
            if (remaining <= 0) return;
            if (remaining > 1.5) Thread.Sleep(1);
            else Thread.SpinWait(20);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint ms);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint ms);
}
