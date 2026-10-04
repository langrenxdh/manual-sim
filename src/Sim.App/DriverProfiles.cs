using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sim.App;

/// <summary>
/// The drivers who share this PC (M11), kept in <c>scores/drivers.json</c>. Each driver has their own
/// score folder: the first driver ever created owns <c>scores/</c> itself, so scores recorded before
/// profiles existed become theirs without moving a file; later drivers get <c>scores/drivers/&lt;id&gt;/</c>.
/// </summary>
public sealed class DriverProfiles
{
    public sealed record Driver(string Id, string Name, string Dir);

    private const string FileName = "drivers.json";

    private readonly string _root;
    private readonly List<Driver> _drivers = [];

    public IReadOnlyList<Driver> Drivers => _drivers;
    public string? LastId { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Scores recorded before profiles existed are waiting for the first driver.</summary>
    public bool HasUnclaimedScores => _drivers.Count == 0 && File.Exists(Path.Combine(_root, "scores.jsonl"));

    public DriverProfiles(string scoresRoot)
    {
        _root = scoresRoot;
        string path = Path.Combine(_root, FileName);
        try
        {
            if (!File.Exists(path)) return;
            var o = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            foreach (var d in o["drivers"]!.AsArray())
                _drivers.Add(new Driver(d!["id"]!.GetValue<string>(), d["name"]!.GetValue<string>(), d["dir"]!.GetValue<string>()));
            LastId = o["last"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or NullReferenceException)
        {
            Error = $"drivers: {ex.Message}";
        }
    }

    public Driver? Last => _drivers.FirstOrDefault(d => d.Id == LastId) ?? _drivers.FirstOrDefault();

    /// <summary>The folder a driver's scores, ghosts and exam results live in.</summary>
    public string DirectoryOf(Driver d) => d.Dir.Length == 0 ? _root : Path.Combine(_root, d.Dir);

    /// <summary>Adds a driver; the first one ever owns the scores folder itself (and any scores already there).</summary>
    public Driver Add(string name)
    {
        name = name.Trim();
        string baseId = Slug(name), id = baseId;
        for (int n = 2; _drivers.Any(d => d.Id == id); n++) id = $"{baseId}-{n}";
        var driver = new Driver(id, name, _drivers.Count == 0 ? "" : Path.Combine("drivers", id));
        _drivers.Add(driver);
        Save();
        return driver;
    }

    public void Choose(Driver d)
    {
        LastId = d.Id;
        Save();
    }

    private void Save()
    {
        var o = new JsonObject
        {
            ["drivers"] = new JsonArray(_drivers.Select(d => (JsonNode)new JsonObject
            {
                ["id"] = d.Id, ["name"] = d.Name, ["dir"] = d.Dir,
            }).ToArray()),
            ["last"] = LastId,
        };
        try
        {
            Directory.CreateDirectory(_root);
            string path = Path.Combine(_root, FileName), tmp = path + ".tmp";
            File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException ex)
        {
            Error = $"drivers: {ex.Message}";
        }
    }

    /// <summary>A folder-safe id from a name: letters and digits kept, the rest become dashes.</summary>
    private static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) && c < 128 ? c : '-');
        string s = sb.ToString().Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Length > 0 ? s : "driver";
    }
}
