using System.Numerics;
using Raylib_cs;
using Sim.Core;
using Sim.Training;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>
/// Replay of a finished exercise attempt (M5): its whole trace from the start, with the ghost (your
/// best completed attempt) drawn faintly behind it on the same time axis, and one line saying where
/// you first parted from your best.
/// </summary>
public static class AttemptReplayView
{
    private const float Margin = 24, PlotGap = 18, TitleHeight = 74, LabelWidth = 70;
    private const double TickEveryS = 1, MinSpeedScaleKmh = 10, SpeedScaleStepKmh = 10;
    private const byte GhostAlpha = 80;

    private static readonly Color Bg = new(12, 12, 16, 235);
    private static readonly Color Axis = new(90, 90, 100, 255);
    private static readonly Color Fg = new(220, 220, 228, 255);
    private static readonly Color Muted = new(140, 140, 150, 255);
    private static readonly Color ClutchColour = new(255, 170, 40, 255);
    private static readonly Color ThrottleColour = new(90, 210, 110, 255);
    private static readonly Color BrakeColour = new(230, 80, 70, 255);
    private static readonly Color RpmColour = new(120, 170, 255, 255);
    private static readonly Color SpeedColour = new(220, 220, 228, 255);
    private static readonly Color Marker = new(255, 220, 90, 255);

    public static void Draw(ScoreResult attempt, IReadOnlyList<TraceSample>? ghost, CoachingParams coaching,
        Rectangle area, VehicleParams p, double tachMaxRpm)
    {
        DrawRectangleRec(area, Bg);
        var trace = attempt.Trace;
        bool isGhost = ghost != null && ReferenceEquals(ghost, trace);
        Ui.Text("Attempt replay   (triangle or P to close)", area.X + Margin, area.Y + 10, 24, Fg);

        string line;
        Divergence? d = null;
        if (ghost == null) line = "No ghost yet: complete this exercise to set one.";
        else if (isGhost) line = "New best: this attempt is now your ghost.";
        else
        {
            d = GhostComparison.First(trace, ghost, coaching);
            line = d == null ? "You followed your best attempt closely all the way." : Describe(d);
        }
        Ui.Text(line, area.X + Margin, area.Y + 42, 19, d != null ? Marker : Muted);
        if (trace.Count < 2) return;

        double duration = Math.Max(trace[^1].TimeS, ghost != null && !isGhost && ghost.Count > 0 ? ghost[^1].TimeS : 0);
        float plotHeight = (area.Height - TitleHeight - Margin - 2 * PlotGap - 24) / 3;
        var plot = new Rectangle(area.X + Margin + LabelWidth, area.Y + TitleHeight, area.Width - 2 * Margin - LabelWidth, plotHeight);
        var others = isGhost ? null : ghost;

        Frame(plot, "pedals", "1", "0", duration);
        Series(others, plot, duration, s => s.Clutch, 0, 1, Faint(ClutchColour));
        Series(others, plot, duration, s => s.Throttle, 0, 1, Faint(ThrottleColour));
        Series(trace, plot, duration, s => s.Clutch, 0, 1, ClutchColour);
        Series(trace, plot, duration, s => s.Throttle, 0, 1, ThrottleColour);
        Series(trace, plot, duration, s => s.Brake, 0, 1, BrakeColour);
        Legend(plot, others != null);
        Mark(plot, duration, d);

        plot.Y += plotHeight + PlotGap;
        Frame(plot, "rpm", tachMaxRpm.ToString("F0"), "0", duration);
        float stallY = plot.Y + plot.Height - (float)(p.Engine.StallRpm / tachMaxRpm) * plot.Height;
        DrawLineV(new Vector2(plot.X, stallY), new Vector2(plot.X + plot.Width, stallY), Axis);
        Series(others, plot, duration, s => s.EngineRpm, 0, tachMaxRpm, Faint(RpmColour));
        Series(trace, plot, duration, s => s.EngineRpm, 0, tachMaxRpm, RpmColour);
        Mark(plot, duration, d);

        plot.Y += plotHeight + PlotGap;
        double maxKmh = MinSpeedScaleKmh;
        foreach (var s in trace) maxKmh = Math.Max(maxKmh, Math.Abs(s.SpeedKmh));
        if (others != null) foreach (var s in others) maxKmh = Math.Max(maxKmh, Math.Abs(s.SpeedKmh));
        maxKmh = Math.Ceiling(maxKmh / SpeedScaleStepKmh) * SpeedScaleStepKmh;
        Frame(plot, "km/h", maxKmh.ToString("F0"), "0", duration);
        Series(others, plot, duration, s => s.SpeedKmh, 0, maxKmh, Faint(SpeedColour));
        Series(trace, plot, duration, s => s.SpeedKmh, 0, maxKmh, SpeedColour);
        Mark(plot, duration, d);

        for (double t = 0; t <= duration + 1e-9; t += TickEveryS)
        {
            float x = plot.X + (float)(t / duration) * plot.Width;
            Ui.Centred($"{t:F0} s", x, plot.Y + plot.Height + 4, 16, Fg);
        }
    }

    /// <summary>Plain-language version of where the attempt left the ghost. Pedals: 0 = up, 1 = floor.</summary>
    public static string Describe(Divergence d) => d.Pedal switch
    {
        "clutch" when d.Current < d.Best =>
            $"At {d.TimeS:F1} s your clutch was further up than in your best ({d.Current:F2} vs {d.Best:F2}): you let it out sooner.",
        "clutch" => $"At {d.TimeS:F1} s you held the clutch down longer than in your best ({d.Current:F2} vs {d.Best:F2}).",
        _ when d.Current > d.Best => $"At {d.TimeS:F1} s you gave more throttle than in your best ({d.Current:F2} vs {d.Best:F2}).",
        _ => $"At {d.TimeS:F1} s you gave less throttle than in your best ({d.Current:F2} vs {d.Best:F2}).",
    };

    private static Color Faint(Color c) => new(c.R, c.G, c.B, GhostAlpha);

    private static void Frame(Rectangle r, string label, string top, string bottom, double duration)
    {
        DrawRectangleLinesEx(r, 1, Axis);
        for (double t = TickEveryS; t < duration; t += TickEveryS)
        {
            float x = r.X + (float)(t / duration) * r.Width;
            DrawLineV(new Vector2(x, r.Y), new Vector2(x, r.Y + r.Height), new Color(40, 40, 48, 255));
        }
        Ui.Text(label, r.X - LabelWidth, r.Y + r.Height / 2 - 10, 20, Fg);
        Ui.Text(top, r.X - Ui.Width(top, 14) - 6, r.Y, 14, Axis);
        Ui.Text(bottom, r.X - Ui.Width(bottom, 14) - 6, r.Y + r.Height - 14, 14, Axis);
    }

    private static void Series(IReadOnlyList<TraceSample>? trace, Rectangle r, double duration, Func<TraceSample, float> get,
        double min, double max, Color colour)
    {
        if (trace == null || trace.Count < 2) return;
        int step = Math.Max(1, trace.Count / Math.Max(1, (int)r.Width));
        Vector2? previous = null;
        for (int i = 0; i < trace.Count; i += step)
        {
            float x = r.X + (float)(trace[i].TimeS / duration) * r.Width;
            double v = Math.Clamp((get(trace[i]) - min) / (max - min), 0, 1);
            var point = new Vector2(x, r.Y + r.Height - (float)v * r.Height);
            if (previous is { } prev) DrawLineEx(prev, point, 2, colour);
            previous = point;
        }
    }

    private static void Mark(Rectangle r, double duration, Divergence? d)
    {
        if (d == null) return;
        float x = r.X + (float)(d.TimeS / duration) * r.Width;
        DrawLineEx(new Vector2(x, r.Y), new Vector2(x, r.Y + r.Height), 2, Marker);
    }

    private static void Legend(Rectangle r, bool withGhost)
    {
        float x = r.X + 8;
        (string, Color)[] items = [("clutch", ClutchColour), ("throttle", ThrottleColour), ("brake", BrakeColour)];
        foreach (var (text, colour) in items)
        {
            DrawRectangle((int)x, (int)r.Y + 8, 14, 4, colour);
            Ui.Text(text, x + 18, r.Y + 1, 15, colour);
            x += 18 + Ui.Width(text, 15) + 16;
        }
        if (withGhost) Ui.Text("faint = your best", x, r.Y + 1, 15, Muted);
    }
}
