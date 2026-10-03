using System.Numerics;
using Raylib_cs;
using Sim.Training;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>
/// Progress view (M5): for each exercise, how many attempts, the best, the recent average, the trend
/// and the issue that most often costs points, with a small chart of the latest scores.
/// </summary>
public static class ProgressView
{
    private const float Margin = 28, RowHeight = 106, ChartWidth = 220;

    private static readonly Color Bg = new(12, 12, 16, 240);
    private static readonly Color Fg = new(225, 225, 232, 255);
    private static readonly Color Muted = new(140, 140, 150, 255);
    private static readonly Color Good = new(90, 210, 110, 255);
    private static readonly Color Warn = new(240, 200, 60, 255);
    private static readonly Color Line = new(120, 170, 255, 255);
    private static readonly Color Axis = new(60, 60, 70, 255);

    public static void Draw(ExerciseConfig config, ScoreHistory history, Rectangle area)
    {
        DrawRectangleRec(area, Bg);
        Ui.Text("Progress", area.X + Margin, area.Y + 14, 32, Fg);
        Ui.Text("Failed attempts count as 0.   E or Esc to close", area.X + Margin, area.Y + 54, 17, Muted);

        float y = area.Y + 90;
        foreach (var e in config.Exercises)
        {
            var p = Progress.Summarise(e.Id, history.Attempts, config.Coaching);
            Ui.Text(e.Name, area.X + Margin, y, 24, Fg);
            if (p.Attempts == 0)
            {
                Ui.Text("not tried yet", area.X + Margin, y + 32, 18, Muted);
                y += RowHeight;
                continue;
            }

            string stats = $"{p.Attempts} attempts, {p.Completed} completed   best {Fmt(p.Best)}   " +
                           $"last {config.Coaching.RecentAttempts}: {Fmt(p.RecentAverage)}";
            Ui.Text(stats, area.X + Margin, y + 32, 18, Fg);
            if (p.TrendPerAttempt is double trend)
            {
                string t = trend >= 0 ? $"improving +{trend:F1} per attempt" : $"slipping {trend:F1} per attempt";
                Ui.Text(t, area.X + Margin, y + 56, 18, trend >= 0 ? Good : Warn);
            }
            if (p.MostCommonIssue != null)
            {
                string issue = $"most often: {ExerciseUi.IssueText(p.MostCommonIssue)}";
                Ui.Text(issue, area.X + Margin, y + 78, 18, Warn);
            }
            Chart(p.RecentScores, new Rectangle(area.X + area.Width - Margin - ChartWidth, y + 4, ChartWidth, RowHeight - 20));
            y += RowHeight;
        }
    }

    private static string Fmt(double? v) => v is double d ? d.ToString("F0") : "-";

    /// <summary>Scores 0-100 of the latest attempts, oldest on the left.</summary>
    private static void Chart(IReadOnlyList<double> scores, Rectangle r)
    {
        DrawRectangleLinesEx(r, 1, Axis);
        if (scores.Count == 0) return;
        Vector2? previous = null;
        for (int i = 0; i < scores.Count; i++)
        {
            float x = scores.Count == 1 ? r.X + r.Width / 2 : r.X + r.Width * i / (scores.Count - 1);
            var point = new Vector2(x, r.Y + r.Height - (float)(scores[i] / 100) * r.Height);
            if (previous is { } prev) DrawLineEx(prev, point, 2, Line);
            DrawCircleV(point, 3, Line);
            previous = point;
        }
    }
}
