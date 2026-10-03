using System.Diagnostics;
using System.Text;

namespace Sim.App;

/// <summary>
/// Telemetry record layout: every input and model state value of one physics step, as float32.
/// The same list names the columns in the file header, so readers never hard-code positions.
/// </summary>
public static class TelemetryFields
{
    public static readonly (string Name, Func<Frame, float> Get)[] All =
    [
        ("timeS", f => (float)f.State.TimeS),
        ("clutchPedalRaw", f => (float)f.Input.ClutchNormalised),
        ("throttlePedalRaw", f => (float)f.Input.ThrottleNormalised),
        ("brakePedalRaw", f => (float)f.Input.BrakeNormalised),
        ("clutchPedal", f => (float)f.Input.Input.Clutch),
        ("throttlePedal", f => (float)f.Input.Input.Throttle),
        ("brakePedal", f => (float)f.Input.Input.Brake),
        ("lever", f => (float)f.Input.Input.Shifter),
        ("handbrake", f => f.Input.Input.Handbrake ? 1 : 0),
        ("starter", f => f.Input.Input.Starter ? 1 : 0),
        ("wheelConnected", f => f.Input.Connected ? 1 : 0),
        ("engineRpm", f => (float)f.State.EngineRpm),
        ("boost", f => (float)f.State.Boost),
        ("throttle", f => (float)f.State.Throttle),
        ("idleControlTorqueNm", f => (float)f.State.IdleControlTorqueNm),
        ("idleControlUsage", f => (float)f.State.IdleControlUsage),
        ("combustionTorqueNm", f => (float)f.State.CombustionTorqueNm),
        ("frictionTorqueNm", f => (float)f.State.FrictionTorqueNm),
        ("stallMarginRpm", f => (float)f.State.StallMarginRpm),
        ("firing", f => f.State.Firing ? 1 : 0),
        ("shudderIntensity", f => (float)f.State.ShudderIntensity),
        ("clutchEngagement", f => (float)f.State.ClutchEngagement),
        ("clutchTorqueNm", f => (float)f.State.ClutchTorqueNm),
        ("clutchLocked", f => f.State.ClutchLocked ? 1 : 0),
        ("inputShaftRpm", f => (float)f.State.InputShaftRpm),
        ("clutchSlipRpm", f => (float)f.State.ClutchSlipRpm),
        ("engagedGear", f => (float)f.State.EngagedGear),
        ("grinding", f => f.State.Grinding ? 1 : 0),
        ("speedMps", f => (float)f.State.SpeedMps),
        ("positionM", f => (float)f.State.PositionM),
        ("accelerationMps2", f => (float)f.State.AccelerationMps2),
        ("grade", f => (float)f.State.Grade),
        ("driveForceN", f => (float)f.State.DriveForceN),
        ("hillHoldState", f => (float)f.State.HillHold),
        ("hillHoldRemainingS", f => (float)f.State.HillHoldRemainingS),
        ("hillHoldForceN", f => (float)f.State.HillHoldForceN),
        ("clutchTempC", f => (float)f.State.ClutchTempC),
        ("clutchFrictionFactor", f => (float)f.State.ClutchFrictionFactor),
        ("engineTempC", f => (float)f.State.EngineTempC),
        ("airCon", f => f.State.AirCon ? 1 : 0),
        ("steeringWheelDeg", f => (float)f.Input.Input.SteeringWheelDeg),
        ("steering", f => f.State.Steering ? 1 : 0),
        ("worldX", f => (float)f.State.WorldX),
        ("worldY", f => (float)f.State.WorldY),
        ("headingRad", f => (float)f.State.HeadingRad),
        ("yawRateRadPerS", f => (float)f.State.YawRateRadPerS),
        ("lateralAccelMps2", f => (float)f.State.LateralAccelMps2),
        ("roadWheelAngleDeg", f => (float)f.State.RoadWheelAngleDeg),
        ("frontSliding", f => f.State.FrontSliding ? 1 : 0),
        ("aligningTorqueNm", f => (float)f.State.AligningTorqueNm),
    ];

    public static int IndexOf(string name) => Array.FindIndex(All, f => f.Name == name);
}

/// <summary>
/// Telemetry file format (<c>telemetry/yyyyMMdd-HHmmss.bin</c>), little-endian:
/// magic "MSIMTLM1", int32 version, int32 field count, then each field name as a length-prefixed
/// UTF-8 string (BinaryWriter.Write(string)), then records of field-count float32 values.
/// </summary>
public static class TelemetryFormat
{
    public const string Magic = "MSIMTLM1";
    public const int Version = 1;

    public static void WriteHeader(BinaryWriter w, IReadOnlyList<string> names)
    {
        w.Write(Encoding.ASCII.GetBytes(Magic));
        w.Write(Version);
        w.Write(names.Count);
        foreach (var n in names) w.Write(n);
    }

    /// <summary>Reads a whole file: column names and the samples, row-major (sample * stride + field).</summary>
    public static (string[] Names, float[] Data) Read(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(r.ReadBytes(Magic.Length)) != Magic)
            throw new InvalidDataException($"{path} is not a telemetry file.");
        int version = r.ReadInt32();
        if (version != Version) throw new InvalidDataException($"{path}: unsupported version {version}.");
        var names = new string[r.ReadInt32()];
        for (int i = 0; i < names.Length; i++) names[i] = r.ReadString();
        long records = (r.BaseStream.Length - r.BaseStream.Position) / (4L * names.Length);
        var data = new float[records * names.Length];
        for (long i = 0; i < data.Length; i++) data[i] = r.ReadSingle();
        return (names, data);
    }
}

/// <summary>The last seconds of telemetry, frozen for the replay view. Row-major, oldest first.</summary>
public sealed record ReplaySnapshot(float[] Data, int Count, int Stride)
{
    public float Get(int sample, int field) => Data[sample * Stride + field];
}

/// <summary>What the telemetry thread reports for display.</summary>
public readonly record struct TelemetryStatus(string? File, long Samples, long Dropped, string? Error);

/// <summary>
/// Telemetry thread: drains every physics step from the ring, appends it to this session's file and
/// keeps the last <see cref="HistoryS"/> seconds in memory for replay. Always on, independent of mode.
/// </summary>
public sealed class TelemetryWriter : IDisposable
{
    public const double HistoryS = 10;
    private const int HistorySamples = 10_000; // HistoryS at 1 kHz
    private const int KeepFiles = 20;
    private const int DrainIntervalMs = 20;

    private readonly SpscRing<Frame> _source;
    private readonly Thread _thread;
    private readonly string _directory;
    private volatile bool _stop;
    private volatile bool _snapshotRequested;
    private ReplaySnapshot? _snapshot;

    public LatestValue<TelemetryStatus> Status { get; } = new();

    public TelemetryWriter(SpscRing<Frame> source, string directory)
    {
        _source = source;
        _directory = directory;
        _thread = new Thread(Run) { Name = "Telemetry", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Asks for a copy of the last seconds; it appears in <see cref="TakeSnapshot"/> shortly after.</summary>
    public void RequestSnapshot()
    {
        Volatile.Write(ref _snapshot, null);
        _snapshotRequested = true;
    }

    /// <summary>The requested snapshot, or null while it is still being prepared.</summary>
    public ReplaySnapshot? TakeSnapshot() => Volatile.Read(ref _snapshot);

    private void Run()
    {
        var fields = TelemetryFields.All;
        int stride = fields.Length;
        var history = new float[HistorySamples * stride];
        long written = 0;
        string? error = null, fileName = null;
        BinaryWriter? file = null;

        try
        {
            try
            {
                Directory.CreateDirectory(_directory);
                PruneOldFiles();
                fileName = Path.Combine(_directory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bin");
                file = new BinaryWriter(new BufferedStream(File.Create(fileName), 1 << 16));
                TelemetryFormat.WriteHeader(file, fields.Select(f => f.Name).ToList());
            }
            catch (IOException ex) { error = ex.Message; file = null; }
            catch (UnauthorizedAccessException ex) { error = ex.Message; file = null; }

            var flushClock = Stopwatch.StartNew();
            while (!_stop)
            {
                while (_source.TryRead(out var frame))
                {
                    int row = (int)(written % HistorySamples) * stride;
                    for (int i = 0; i < stride; i++)
                    {
                        float v = fields[i].Get(frame);
                        history[row + i] = v;
                        file?.Write(v);
                    }
                    written++;
                }

                if (_snapshotRequested)
                {
                    _snapshotRequested = false;
                    Volatile.Write(ref _snapshot, Snapshot(history, written, stride));
                }
                if (file != null && flushClock.ElapsedMilliseconds >= 1000)
                {
                    file.Flush();
                    flushClock.Restart();
                }
                Status.Publish(new TelemetryStatus(fileName, written, _source.Dropped, error));
                Thread.Sleep(DrainIntervalMs);
            }
        }
        finally
        {
            file?.Dispose();
        }
    }

    private static ReplaySnapshot Snapshot(float[] history, long written, int stride)
    {
        int count = (int)Math.Min(written, HistorySamples);
        var data = new float[count * stride];
        long first = written - count;
        for (int s = 0; s < count; s++)
        {
            int src = (int)((first + s) % HistorySamples) * stride;
            Array.Copy(history, src, data, s * stride, stride);
        }
        return new ReplaySnapshot(data, count, stride);
    }

    /// <summary>Keeps the newest files so that, with this session's, there are at most <see cref="KeepFiles"/>.</summary>
    private void PruneOldFiles()
    {
        var old = new DirectoryInfo(_directory).GetFiles("*.bin")
            .OrderByDescending(f => f.Name)
            .Skip(KeepFiles - 1);
        foreach (var f in old) f.Delete();
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
    }
}
