using System.Text.Json;
using System.Text.Json.Nodes;
using Sim.Training;

namespace Sim.App;

/// <summary>
/// Every finished attempt, one JSON object per line in <c>scores/scores.jsonl</c> (not in git).
/// Keeps each exercise's best completed score in memory for the result card.
/// </summary>
public sealed class ScoreHistory
{
    private readonly string _path;
    private readonly Dictionary<string, double> _best = [];

    public string? Error { get; private set; }

    public ScoreHistory(string directory)
    {
        _path = Path.Combine(directory, "scores.jsonl");
        try
        {
            if (!File.Exists(_path)) return;
            foreach (var line in File.ReadLines(_path))
            {
                if (JsonNode.Parse(line) is not JsonObject o) continue;
                if (o["failed"]?.GetValue<bool>() == true) continue;
                Track(o["exercise"]!.GetValue<string>(), o["score"]!.GetValue<double>());
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            Error = $"score history: {ex.Message}";
        }
    }

    /// <summary>Best completed score for an exercise, or null if never completed.</summary>
    public double? Best(string exerciseId) => _best.TryGetValue(exerciseId, out double b) ? b : null;

    public void Record(ScoreResult r)
    {
        var m = r.Metrics;
        var line = new JsonObject
        {
            ["time"] = DateTime.Now.ToString("s"),
            ["exercise"] = r.ExerciseId,
            ["score"] = Math.Round(r.Score, 1),
            ["grade"] = r.Grade.ToString(),
            ["failed"] = r.Failed,
            ["reason"] = r.FailReason,
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
        }
        catch (IOException ex)
        {
            Error = $"score history: {ex.Message}";
        }
        if (!r.Failed) Track(r.ExerciseId, r.Score);
    }

    private void Track(string id, double score)
    {
        if (!_best.TryGetValue(id, out double b) || score > b) _best[id] = score;
    }
}
