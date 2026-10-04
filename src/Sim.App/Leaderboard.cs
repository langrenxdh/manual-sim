using Raylib_cs;
using Sim.Training;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>
/// Every driver's best score per exercise and exam, side by side (M11), with the top score in each row
/// highlighted. The current driver's history is the live one; the others are read from their folders
/// when the driver changes.
/// </summary>
public sealed class Leaderboard
{
    private const float RowHeight = 30, NameColumn = 300, DriverColumn = 120;
    private const int MaxDrivers = 6;

    private static readonly Color Bg = new(12, 12, 16, 240);
    private static readonly Color Fg = new(225, 225, 232, 255);
    private static readonly Color Muted = new(140, 140, 150, 255);
    private static readonly Color Top = new(90, 210, 110, 255);
    private static readonly Color Current = new(255, 170, 40, 255);

    private readonly List<(DriverProfiles.Driver Driver, ScoreHistory History)> _drivers = [];
    private string? _currentId;

    public bool Open { get; set; }

    /// <summary>Re-reads every other driver's scores; the current driver uses the live <paramref name="history"/>.</summary>
    public void Refresh(DriverProfiles profiles, DriverProfiles.Driver current, ScoreHistory history)
    {
        _drivers.Clear();
        _currentId = current.Id;
        foreach (var d in profiles.Drivers)
            _drivers.Add((d, d.Id == current.Id ? history : new ScoreHistory(profiles.DirectoryOf(d))));
    }

    /// <summary>The best completed score of any driver, and whose it is.</summary>
    public (double Score, string Name)? Record(string historyId)
    {
        (double, string)? best = null;
        foreach (var (d, h) in _drivers)
            if (h.Best(historyId) is double b && (best is not { } r || b > r.Item1)) best = (b, d.Name);
        return best;
    }

    public void Draw(ExerciseConfig config, Rectangle area)
    {
        if (!Open) return;
        var drivers = _drivers.Take(MaxDrivers).ToList();
        var rows = config.Exercises.Select(e => (Id: e.Id, Name: e.Name))
            .Concat(config.Exams.Select(x => (Id: ExerciseUi.ExamHistoryId(x), Name: "Exam: " + x.Name))).ToList();
        float w = Math.Min(NameColumn + DriverColumn * Math.Max(1, drivers.Count) + 48, area.Width - 16);
        float driverColumn = drivers.Count == 0 ? 0 : (w - 48 - NameColumn) / drivers.Count;
        float h = Math.Min(110 + (rows.Count + 1) * RowHeight + 20, area.Height - 20);
        var r = new Rectangle(area.X + (area.Width - w) / 2, area.Y + 10, w, h);
        DrawRectangleRounded(r, 0.03f, 6, Bg);
        Ui.Text("Leaderboard", r.X + 24, r.Y + 16, 30, Fg);
        Ui.Text("Best score of each driver.   K or Esc closes", r.X + 24, r.Y + 54, 17, Muted);

        float y = r.Y + 92, x0 = r.X + 24;
        for (int k = 0; k < drivers.Count; k++)
            Ui.Text(drivers[k].Driver.Name, x0 + NameColumn + k * driverColumn, y, 19, drivers[k].Driver.Id == _currentId ? Current : Muted);
        y += RowHeight;
        foreach (var (id, name) in rows)
        {
            if (y + RowHeight > r.Y + r.Height) break;
            Ui.Text(name, x0, y, 18, Fg);
            double? top = drivers.Select(d => d.History.Best(id)).Max();
            for (int k = 0; k < drivers.Count; k++)
            {
                double? b = drivers[k].History.Best(id);
                string text = b is double v ? $"{v:F0}" : "-";
                Ui.Text(text, x0 + NameColumn + k * driverColumn, y, 19, b != null && b == top ? Top : Muted);
            }
            y += RowHeight;
        }
    }
}
