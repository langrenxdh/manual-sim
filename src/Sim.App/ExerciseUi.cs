using Raylib_cs;
using Sim.Input;
using Sim.Training;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>
/// Graded-exercise screens (docs/design.en.md, Practice and scoring): the menu (E), a banner with
/// the goal and live metrics while an attempt runs, and the result card when it ends.
/// </summary>
public sealed class ExerciseUi
{
    private const float MenuWidth = 640, RowHeight = 56, CardWidth = 640;

    private static readonly Color Bg = new(12, 12, 16, 235);
    private static readonly Color Fg = new(225, 225, 232, 255);
    private static readonly Color Muted = new(140, 140, 150, 255);
    private static readonly Color Highlight = new(60, 90, 140, 255);
    private static readonly Color Good = new(90, 210, 110, 255);
    private static readonly Color Warn = new(240, 200, 60, 255);
    private static readonly Color Bad = new(235, 70, 60, 255);

    private int _selected, _scroll;
    private UiButtons _previous;

    public bool MenuOpen { get; set; }

    public enum MenuChoice { None, Exercise, Exam, Progress, FreeDriving }

    /// <summary>Menu rows: the exercises, then the exams, then "Progress", then "Free driving".</summary>
    private static int RowCount(ExerciseConfig c) => c.Exercises.Length + c.Exams.Length + 2;

    /// <summary>Menu input; the chosen exercise or exam comes back through the out parameters.</summary>
    public MenuChoice UpdateMenu(ExerciseConfig config, UiButtons buttons, out ExerciseDef? exercise, out ExamDef? exam)
    {
        exercise = null;
        exam = null;
        bool up = IsKeyPressed(KeyboardKey.Up) || (buttons.PadUp && !_previous.PadUp);
        bool down = IsKeyPressed(KeyboardKey.Down) || (buttons.PadDown && !_previous.PadDown);
        bool choose = IsKeyPressed(KeyboardKey.Enter) || IsKeyPressed(KeyboardKey.KpEnter) || (buttons.PadRight && !_previous.PadRight);
        _previous = buttons;
        if (!MenuOpen) return MenuChoice.None;

        int count = RowCount(config);
        if (up) _selected = (_selected + count - 1) % count;
        if (down) _selected = (_selected + 1) % count;
        _selected = Math.Clamp(_selected, 0, count - 1);
        if (!choose) return MenuChoice.None;

        MenuOpen = false;
        int n = config.Exercises.Length, m = config.Exams.Length;
        if (_selected < n)
        {
            exercise = config.Exercises[_selected];
            return MenuChoice.Exercise;
        }
        if (_selected < n + m)
        {
            exam = config.Exams[_selected - n];
            return MenuChoice.Exam;
        }
        return _selected == n + m ? MenuChoice.Progress : MenuChoice.FreeDriving;
    }

    public void DrawMenu(ExerciseConfig config, ScoreHistory history, Rectangle area)
    {
        if (!MenuOpen) return;
        int count = RowCount(config);
        int exams = config.Exams.Length;
        float mw = Math.Min(MenuWidth, area.Width - 16);
        // Scroll when the rows do not fit the road view: keep the selected row visible.
        int fits = Math.Max(1, (int)((area.Height - 40 - 90 - 20) / RowHeight));
        int visible = Math.Min(count, fits);
        _scroll = Math.Clamp(_scroll, Math.Max(0, _selected - visible + 1), Math.Min(_selected, count - visible));
        var r = new Rectangle(area.X + (area.Width - mw) / 2, area.Y + 40, mw, 90 + visible * RowHeight + 20);
        DrawRectangleRounded(r, 0.04f, 6, Bg);
        Ui.Text("Exercises", r.X + 24, r.Y + 18, 32, Fg);
        Ui.Text("Up/Down (or D-pad) select, Enter (or D-pad right) start, Esc close", r.X + 24, r.Y + 58, 17, Muted);
        if (_scroll > 0) Ui.Text("^ more", r.X + r.Width - 90, r.Y + 22, 16, Muted);
        if (_scroll + visible < count) Ui.Text("v more", r.X + r.Width - 90, r.Y + r.Height - 22, 16, Muted);

        for (int i = _scroll; i < _scroll + visible; i++)
        {
            float y = r.Y + 90 + (i - _scroll) * RowHeight;
            if (i == _selected) DrawRectangle((int)r.X + 12, (int)y, (int)r.Width - 24, (int)RowHeight - 4, Highlight);
            int n = config.Exercises.Length;
            if (i >= n && i < n + exams)
            {
                var x = config.Exams[i - n];
                Ui.Text($"Exam: {x.Name}", r.X + 28, y + 4, 24, Warn);
                Ui.Text($"{x.Parts.Length} parts in a row, no retries; pass with every part done and an average of {x.PassAverage:F0}.",
                    r.X + 28, y + 30, 15, Muted);
                string examBest = history.Best(ExamHistoryId(x)) is double eb ? $"best {eb:F0}" : "not passed yet";
                Ui.Text(examBest, r.X + r.Width - 28 - Ui.Width(examBest, 17), y + 8, 17, history.Best(ExamHistoryId(x)) is null ? Muted : Good);
                continue;
            }
            if (i == n + exams)
            {
                Ui.Text("Progress", r.X + 28, y + 4, 24, Fg);
                Ui.Text("Scores over time, trend and what most often costs points.", r.X + 28, y + 30, 15, Muted);
                continue;
            }
            if (i == n + exams + 1)
            {
                Ui.Text("Free driving", r.X + 28, y + 14, 24, Fg);
                continue;
            }
            var e = config.Exercises[i];
            Ui.Text(e.Name, r.X + 28, y + 4, 24, Fg);
            Ui.Text(e.Goal, r.X + 28, y + 30, 15, Muted);
            string best = history.Best(e.Id) is double b ? $"best {b:F0}" : "not done yet";
            Ui.Text(best, r.X + r.Width - 28 - Ui.Width(best, 17), y + 8, 17, history.Best(e.Id) is null ? Muted : Good);
        }
    }

    /// <summary>Top-centre banner while an attempt runs: what to do and how it is going.</summary>
    public static void DrawBanner(in ExerciseStatus x, Rectangle road)
    {
        if (x.Exercise is not { } e || x.Phase != AttemptPhase.Running) return;
        var m = x.Metrics;
        var l = x.Live;
        string live = $"{m.ElapsedS:F1} s   clutch heat {m.ClutchSlipEnergyKJ:F1} kJ   jerk {m.PeakJerkMps3:F0}" +
                      (e.MaxRollbackM != null ? $"   rollback {m.RollbackM:F2} m" : "") +
                      (m.GrindingS > 0 ? $"   grinding {m.GrindingS:F1} s" : "") +
                      (l.GapM is double gap ? $"   gap {gap:F1} m" : "") +
                      (e.StopLine != null ? l.StoppedAtLine ? $"   stopped {m.StopErrorM:F1} m off" : "   stop at the line" : "") +
                      (e.SpeedLimitKmh is double limit ? $"   limit {limit:F0} km/h, over {m.OverSpeedS:F1} s, brake {m.BrakeS:F1} s" : "") +
                      (e.Downshift is { } d ? l.DownshiftDone ? $"   rev mismatch {m.RevMatchErrorRpm:F0} rpm"
                          : l.DownshiftArmed ? $"   now shift down to {d.ToGear}" : $"   reach {d.MinSpeedKmh:F0} km/h in {d.FromGear}" : "") +
                      (e.TownStart != null ? $"   off road {m.OffRoadS:F1} s{(l.OffRoad ? "  OFF THE ROAD" : "")}" : "");
        float w = Math.Min(Math.Max(Ui.Width(e.Goal, 18), Ui.Width(live, 18)) + 40, road.Width - 8);
        var r = new Rectangle(road.X + (road.Width - w) / 2, road.Y + 12, w, 92);
        DrawRectangleRounded(r, 0.15f, 6, Bg);
        Ui.Centred(e.Name, r.X + w / 2, r.Y + 8, 24, Fg);
        Ui.Centred(e.Goal, r.X + w / 2, r.Y + 38, 18, Muted);
        Ui.Centred(live, r.X + w / 2, r.Y + 62, 18, Fg);
    }

    /// <summary>The result of the attempt that just ended.</summary>
    public static void DrawResult(in ExerciseStatus x, ScoreHistory history, Rectangle area)
    {
        if (x.Exercise is not { } e || x.Result is not { } res) return;
        float h = 210 + res.Penalties.Count * 30 + 60;
        float cw = Math.Min(CardWidth, area.Width - 16);
        var r = new Rectangle(area.X + (area.Width - cw) / 2, area.Y + 30, cw, h);
        DrawRectangleRounded(r, 0.04f, 6, Bg);
        float x0 = r.X + 28, y = r.Y + 18;

        Ui.Text(e.Name, x0, y, 30, Fg);
        string outcome = res.Failed ? $"Failed: {res.FailReason}" : "Completed";
        Ui.Text(outcome, x0, y + 38, 22, res.Failed ? Bad : Good);

        string score = $"{res.Score:F0}";
        Color gradeColour = res.Failed ? Bad : res.Grade is 'A' or 'B' ? Good : Warn;
        Ui.Text(score, r.X + r.Width - 28 - Ui.Width(score, 64) - 60, y - 4, 64, gradeColour);
        Ui.Text(res.Failed ? "-" : res.Grade.ToString(), r.X + r.Width - 28 - 44, y + 4, 52, gradeColour);

        y += 82;
        string best = history.Best(e.Id) is double b ? $"Best: {b:F0}" : "Best: none completed yet";
        Ui.Text(best, x0, y, 18, Muted);
        y += 34;

        Ui.Text("metric", x0, y, 16, Muted);
        Ui.Text("value", x0 + cw * 0.42f, y, 16, Muted);
        Ui.Text("points lost", x0 + cw * 0.68f, y, 16, Muted);
        y += 26;
        var biggest = res.Biggest;
        foreach (var p in res.Penalties)
        {
            Color c = p == biggest ? Warn : Fg;
            Ui.Text(Label(p.Metric), x0, y, 19, c);
            Ui.Text(Value(p.Metric, p.Value), x0 + cw * 0.42f, y, 19, c);
            Ui.Text($"{p.Points:F0} / {p.Weight:F0}", x0 + cw * 0.68f, y, 19, c);
            y += 30;
        }

        y += 8;
        string advice = biggest != null ? $"Work on: {Advice(biggest.Metric)}" : res.Failed ? "" : "Clean run.";
        Ui.Text(advice, x0, y, 19, biggest != null ? Warn : Good);
        Ui.Text("R retry   E exercises   Esc free driving   triangle replay", x0, r.Y + h - 32, 17, Muted);
    }

    /// <summary>The id an exam's overall result is kept under in the score history.</summary>
    public static string ExamHistoryId(ExamDef exam) => "exam:" + exam.Id;

    /// <summary>Under the result card during an exam: which part this was and what comes next.</summary>
    public static void DrawExamProgress(ExamRun run, Rectangle area)
    {
        string text = run.Done ? "Exam finished: Enter shows the result." : $"Exam part {run.PartNumber - 1} of {run.Exam.Parts.Length} done.   Enter: next part";
        float w = Ui.Width(text, 20) + 40;
        var r = new Rectangle(area.X + (area.Width - w) / 2, area.Y + area.Height - 60, w, 40);
        DrawRectangleRounded(r, 0.3f, 6, Bg);
        Ui.Centred(text, r.X + w / 2, r.Y + 10, 20, Warn);
    }

    /// <summary>The exam verdict: every part's score and whether it passed.</summary>
    public static void DrawExamSummary(ExamRun run, ExerciseConfig config, Rectangle area)
    {
        float cw = Math.Min(CardWidth, area.Width - 16);
        float h = 150 + run.Results.Count * 30 + 50;
        var r = new Rectangle(area.X + (area.Width - cw) / 2, area.Y + 30, cw, h);
        DrawRectangleRounded(r, 0.04f, 6, Bg);
        float x0 = r.X + 28, y = r.Y + 18;
        bool passed = run.Passed == true;
        Ui.Text($"Exam: {run.Exam.Name}", x0, y, 30, Fg);
        Ui.Text(passed ? "PASSED" : "NOT PASSED", x0, y + 40, 26, passed ? Good : Bad);
        string avg = $"average {run.Average:F0} (pass {run.Exam.PassAverage:F0})";
        Ui.Text(avg, r.X + r.Width - 28 - Ui.Width(avg, 20), y + 46, 20, Fg);
        y += 90;
        foreach (var res in run.Results)
        {
            string name = config.Exercises.FirstOrDefault(e => e.Id == res.ExerciseId)?.Name ?? res.ExerciseId;
            Ui.Text(name, x0, y, 19, Fg);
            string outcome = res.Failed ? $"failed: {res.FailReason}" : $"{res.Score:F0}  {res.Grade}";
            Ui.Text(outcome, x0 + cw * 0.55f, y, 19, res.Failed ? Bad : Fg);
            y += 30;
        }
        Ui.Text("Enter or E: exercises   Esc: free driving", x0, r.Y + h - 32, 17, Muted);
    }

    /// <summary>A history "main issue" (a metric name or a failure reason) in words.</summary>
    public static string IssueText(string issue) =>
        Enum.TryParse<Metric>(issue, out var m) ? Label(m).ToLowerInvariant() : issue;

    private static string Label(Metric m) => m switch
    {
        Metric.ClutchSlipEnergyKJ => "Clutch heat",
        Metric.PeakJerkMps3 => "Lurch",
        Metric.RollbackM => "Rollback",
        Metric.GrindingS => "Grinding",
        Metric.OverRevS => "Over-rev",
        Metric.TimeS => "Time",
        Metric.OffRoadS => "Off the road",
        _ => m.ToString(),
    };

    private static string Value(Metric m, double v) => m switch
    {
        Metric.ClutchSlipEnergyKJ => $"{v:F1} kJ",
        Metric.PeakJerkMps3 => $"{v:F0} m/s^3",
        Metric.RollbackM => $"{v:F2} m",
        _ => $"{v:F1} s",
    };

    private static string Advice(Metric m) => m switch
    {
        Metric.ClutchSlipEnergyKJ => "less throttle while the clutch slips, and don't hold the bite too long",
        Metric.PeakJerkMps3 => "let the clutch out more gradually once it bites",
        Metric.RollbackM => "find the bite before releasing the brake or handbrake",
        Metric.GrindingS => "press the clutch fully before moving the lever",
        Metric.OverRevS => "less throttle; stay out of the red zone",
        Metric.TimeS => "commit a little sooner once the clutch bites",
        Metric.OffRoadS => "slower, and turn in later or earlier so every corner stays on the road",
        _ => m.ToString(),
    };
}
