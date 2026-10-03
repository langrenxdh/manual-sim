using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sim.Training;

namespace Sim.App;

/// <summary>
/// Every finished attempt, one JSON object per line in <c>scores/scores.jsonl</c> (not in git), plus
/// each exercise's ghost: the trace of its best completed attempt in <c>scores/ghosts/&lt;id&gt;.bin</c>.
/// </summary>
public sealed class ScoreHistory
{
    private const string GhostMagic = "MSIMGHO1";
    private const int TraceFields = 6;

    private readonly string _path;
    private readonly string _ghostDir;
    private readonly List<AttemptRecord> _attempts = [];
    private readonly Dictionary<string, double> _best = [];
    private readonly Dictionary<string, IReadOnlyList<TraceSample>?> _ghosts = [];

    public string? Error { get; private set; }
    public IReadOnlyList<AttemptRecord> Attempts => _attempts;

    public ScoreHistory(string directory)
    {
        _path = Path.Combine(directory, "scores.jsonl");
        _ghostDir = Path.Combine(directory, "ghosts");
        try
        {
            if (!File.Exists(_path)) return;
            foreach (var line in File.ReadLines(_path))
            {
                if (JsonNode.Parse(line) is not JsonObject o) continue;
                var record = new AttemptRecord(
                    DateTime.Parse(o["time"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                    o["exercise"]!.GetValue<string>(),
                    o["score"]!.GetValue<double>(),
                    o["failed"]?.GetValue<bool>() == true,
                    o["mainIssue"]?.GetValue<string>() ?? o["reason"]?.GetValue<string>());
                Add(record);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException)
        {
            Error = $"score history: {ex.Message}";
        }
    }

    /// <summary>Best completed score for an exercise, or null if never completed.</summary>
    public double? Best(string exerciseId) => _best.TryGetValue(exerciseId, out double b) ? b : null;

    /// <summary>Appends the result; a new best also becomes the exercise's ghost.</summary>
    public void Record(ScoreResult r)
    {
        bool newBest = !r.Failed && (Best(r.ExerciseId) is not double b || r.Score > b);
        var m = r.Metrics;
        var record = new AttemptRecord(DateTime.Now, r.ExerciseId, r.Score, r.Failed, r.MainIssue);
        var line = new JsonObject
        {
            ["time"] = record.Time.ToString("s", CultureInfo.InvariantCulture),
            ["exercise"] = r.ExerciseId,
            ["score"] = Math.Round(r.Score, 1),
            ["grade"] = r.Grade.ToString(),
            ["failed"] = r.Failed,
            ["reason"] = r.FailReason,
            ["mainIssue"] = r.MainIssue,
            ["elapsedS"] = Math.Round(m.ElapsedS, 2),
            ["clutchSlipEnergyKJ"] = Math.Round(m.ClutchSlipEnergyKJ, 2),
            ["peakJerkMps3"] = Math.Round(m.PeakJerkMps3, 1),
            ["rollbackM"] = Math.Round(m.RollbackM, 3),
            ["grindingS"] = Math.Round(m.GrindingS, 2),
            ["overRevS"] = Math.Round(m.OverRevS, 2),
            ["stalls"] = m.Stalls,
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, line.ToJsonString() + "\n");
            if (newBest) SaveGhost(r.ExerciseId, r.Trace);
        }
        catch (IOException ex)
        {
            Error = $"score history: {ex.Message}";
        }
        Add(record);
    }

    /// <summary>The best completed attempt's trace, or null if there is none yet.</summary>
    public IReadOnlyList<TraceSample>? Ghost(string exerciseId)
    {
        if (_ghosts.TryGetValue(exerciseId, out var cached)) return cached;
        IReadOnlyList<TraceSample>? ghost = null;
        try
        {
            string path = GhostPath(exerciseId);
            if (File.Exists(path)) ghost = LoadGhost(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
        {
            Error = $"ghost: {ex.Message}";
        }
        _ghosts[exerciseId] = ghost;
        return ghost;
    }

    private void Add(AttemptRecord r)
    {
        _attempts.Add(r);
        if (!r.Failed && (!_best.TryGetValue(r.ExerciseId, out double b) || r.Score > b)) _best[r.ExerciseId] = r.Score;
    }

    private string GhostPath(string id) => Path.Combine(_ghostDir, id + ".bin");

    private void SaveGhost(string id, IReadOnlyList<TraceSample> trace)
    {
        Directory.CreateDirectory(_ghostDir);
        string path = GhostPath(id), tmp = path + ".tmp";
        using (var w = new BinaryWriter(File.Create(tmp)))
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes(GhostMagic));
            w.Write(TraceFields);
            w.Write(trace.Count);
            foreach (var s in trace)
            {
                w.Write(s.TimeS); w.Write(s.Clutch); w.Write(s.Throttle); w.Write(s.Brake); w.Write(s.EngineRpm); w.Write(s.SpeedKmh);
            }
        }
        File.Move(tmp, path, overwrite: true);
        _ghosts[id] = trace;
    }

    private static TraceSample[] LoadGhost(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (System.Text.Encoding.ASCII.GetString(r.ReadBytes(GhostMagic.Length)) != GhostMagic || r.ReadInt32() != TraceFields)
            throw new InvalidDataException($"{path} is not a ghost file.");
        var samples = new TraceSample[r.ReadInt32()];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = new TraceSample(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        return samples;
    }
}
