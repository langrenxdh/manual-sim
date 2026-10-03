namespace Sim.App;

/// <summary>
/// Finds the repository's <c>config/</c> directory (walking up from the executable), so that
/// saving from the tuning panel edits the real files rather than a copy in the build output.
/// </summary>
public sealed class ConfigFiles
{
    public const string VehicleFile = "golf-110tsi.json";
    public const string InputFile = "g29.json";
    public const string SoundFile = "engine-sound.json";

    public string Directory { get; }

    private ConfigFiles(string directory) => Directory = directory;

    public static ConfigFiles Locate()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "config");
            if (File.Exists(Path.Combine(candidate, VehicleFile)))
                return new ConfigFiles(candidate);
        }
        throw new FileNotFoundException($"Could not find config/{VehicleFile} above {AppContext.BaseDirectory}.");
    }

    public string PathOf(string name) => Path.Combine(Directory, name);

    public string Read(string name) => File.ReadAllText(PathOf(name));

    /// <summary>Writes new JSON, keeping the file's leading <c>//</c> comment lines.</summary>
    public void Save(string name, string json)
    {
        string path = PathOf(name);
        var header = File.Exists(path)
            ? File.ReadLines(path).TakeWhile(l => l.TrimStart().StartsWith("//")).ToList()
            : [];
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, string.Join('\n', header.Append(json)) + "\n");
        File.Move(tmp, path, overwrite: true);
    }
}
