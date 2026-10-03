using System.Numerics;
using Raylib_cs;
using Sim.Core;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>
/// Replay of the last seconds (design doc: immersion mode, replay): pedals, engine speed and road
/// speed on a shared time axis, with each stall marked, so you can see exactly when the clutch
/// came up too fast. Draws a frozen <see cref="ReplaySnapshot"/>; physics keeps running underneath.
/// </summary>
public static class ReplayView
{
    private const float Margin = 24, PlotGap = 18, TitleHeight = 40, LabelWidth = 70;
    private const double SampleRateHz = 1000;
    private const double TickEveryS = 1;
    private const double MinSpeedScaleKmh = 10;
    private const double SpeedScaleStepKmh = 10;

    private static readonly Color Bg = new(12, 12, 16, 235);
    private static readonly Color Axis = new(90, 90, 100, 255);
    private static readonly Color Fg = new(220, 220, 228, 255);
    private static readonly Color ClutchColour = new(255, 170, 40, 255);
    private static readonly Color ThrottleColour = new(90, 210, 110, 255);
    private static readonly Color BrakeColour = new(230, 80, 70, 255);
    private static readonly Color RpmColour = new(120, 170, 255, 255);
    private static readonly Color SpeedColour = new(220, 220, 228, 255);
    private static readonly Color StallColour = new(255, 60, 60, 255);
    private static readonly Color RefLine = new(150, 150, 160, 255);

    private static readonly int Clutch = TelemetryFields.IndexOf("clutchPedal");
    private static readonly int Throttle = TelemetryFields.IndexOf("throttlePedal");
    private static readonly int Brake = TelemetryFields.IndexOf("brakePedal");
    private static readonly int Rpm = TelemetryFields.IndexOf("engineRpm");
    private static readonly int Speed = TelemetryFields.IndexOf("speedMps");
    private static readonly int Firing = TelemetryFields.IndexOf("firing");

    public static void Draw(ReplaySnapshot? snap, Rectangle area, VehicleParams p, double tachMaxRpm)
    {
        DrawRectangleRec(area, Bg);
        Ui.Text("Replay - last 10 s   (triangle or P to close)", area.X + Margin, area.Y + 10, 24, Fg);
        if (snap == null || snap.Count < 2)
        {
            Ui.Text("preparing...", area.X + Margin, area.Y + TitleHeight + 10, 22, Fg);
            return;
        }

        float plotsTop = area.Y + TitleHeight;
        float plotHeight = (area.Height - TitleHeight - Margin - 2 * PlotGap - 24) / 3;
        var plot = new Rectangle(area.X + Margin + LabelWidth, plotsTop, area.Width - 2 * Margin - LabelWidth, plotHeight);
        double durationS = (snap.Count - 1) / SampleRateHz;
        var stalls = StallIndices(snap, p.Engine.StallRpm);

        // Pedals, 0..1 as the physics saw them (after filtering).
        Frame(plot, "pedals", "1", "0", durationS);
        Series(snap, plot, Clutch, 0, 1, ClutchColour);
        Series(snap, plot, Throttle, 0, 1, ThrottleColour);
        Series(snap, plot, Brake, 0, 1, BrakeColour);
        Legend(plot, ("clutch", ClutchColour), ("throttle", ThrottleColour), ("brake", BrakeColour));
        Stalls(snap, plot, stalls);

        // Engine speed with the stall threshold and idle speed for reference.
        plot.Y += plotHeight + PlotGap;
        Frame(plot, "rpm", tachMaxRpm.ToString("F0"), "0", durationS);
        RefY(plot, p.Engine.StallRpm, 0, tachMaxRpm, "stall", labelRight: true);
        RefY(plot, p.Engine.IdleRpm, 0, tachMaxRpm, "idle");
        Series(snap, plot, Rpm, 0, tachMaxRpm, RpmColour);
        Stalls(snap, plot, stalls);

        // Road speed, scaled to what happened.
        plot.Y += plotHeight + PlotGap;
        double maxKmh = MinSpeedScaleKmh;
        for (int i = 0; i < snap.Count; i++) maxKmh = Math.Max(maxKmh, Math.Abs(snap.Get(i, Speed)) * 3.6);
        maxKmh = Math.Ceiling(maxKmh / SpeedScaleStepKmh) * SpeedScaleStepKmh;
        Frame(plot, "km/h", maxKmh.ToString("F0"), "0", durationS);
        Series(snap, plot, Speed, 0, maxKmh / 3.6, SpeedColour);
        Stalls(snap, plot, stalls);

        // Time axis labels under the last plot: seconds before now.
        for (double t = 0; t <= durationS + 1e-9; t += TickEveryS)
        {
            float x = plot.X + plot.Width - (float)(t / durationS) * plot.Width;
            Ui.Centred(t == 0 ? "now" : $"-{t:F0} s", x, plot.Y + plot.Height + 4, 16, Fg);
        }
    }

    /// <summary>Samples where combustion stopped below the stall threshold.</summary>
    private static List<int> StallIndices(ReplaySnapshot s, double stallRpm)
    {
        var result = new List<int>();
        for (int i = 1; i < s.Count; i++)
        {
            if (s.Get(i - 1, Firing) > 0.5f && s.Get(i, Firing) < 0.5f && s.Get(i, Rpm) < stallRpm)
                result.Add(i);
        }
        return result;
    }

    private static void Frame(Rectangle r, string label, string top, string bottom, double durationS)
    {
        DrawRectangleLinesEx(r, 1, Axis);
        for (double t = TickEveryS; t < durationS; t += TickEveryS)
        {
            float x = r.X + r.Width - (float)(t / durationS) * r.Width;
            DrawLineV(new Vector2(x, r.Y), new Vector2(x, r.Y + r.Height), new Color(40, 40, 48, 255));
        }
        Ui.Text(label, r.X - LabelWidth, r.Y + r.Height / 2 - 10, 20, Fg);
        Ui.Text(top, r.X - Ui.Width(top, 14) - 6, r.Y, 14, Axis);
        Ui.Text(bottom, r.X - Ui.Width(bottom, 14) - 6, r.Y + r.Height - 14, 14, Axis);
    }

    private static void Series(ReplaySnapshot s, Rectangle r, int field, double min, double max, Color colour)
    {
        // At most about one point per pixel column.
        int step = Math.Max(1, s.Count / Math.Max(1, (int)r.Width));
        Vector2? previous = null;
        for (int i = 0; i < s.Count; i += step)
        {
            float x = r.X + r.Width * i / (s.Count - 1);
            double v = Math.Clamp((s.Get(i, field) - min) / (max - min), 0, 1);
            var point = new Vector2(x, r.Y + r.Height - (float)v * r.Height);
            if (previous is { } prev) DrawLineEx(prev, point, 2, colour);
            previous = point;
        }
    }

    private static void RefY(Rectangle r, double value, double min, double max, string label, bool labelRight = false)
    {
        float y = r.Y + r.Height - (float)((value - min) / (max - min)) * r.Height;
        DrawLineV(new Vector2(r.X, y), new Vector2(r.X + r.Width, y), RefLine);
        float x = labelRight ? r.X + r.Width - Ui.Width(label, 14) - 4 : r.X + 4;
        Ui.Text(label, x, y - 16, 14, RefLine);
    }

    private static void Stalls(ReplaySnapshot s, Rectangle r, List<int> stalls)
    {
        foreach (int i in stalls)
        {
            float x = r.X + r.Width * i / (s.Count - 1);
            DrawLineEx(new Vector2(x, r.Y), new Vector2(x, r.Y + r.Height), 2, StallColour);
            Ui.Text("stall", x + 4, r.Y + 2, 14, StallColour);
        }
    }

    private static void Legend(Rectangle r, params (string Text, Color Colour)[] items)
    {
        float x = r.X + 8;
        foreach (var (text, colour) in items)
        {
            DrawRectangle((int)x, (int)r.Y + 8, 14, 4, colour);
            Ui.Text(text, x + 18, r.Y + 1, 15, colour);
            x += 18 + Ui.Width(text, 15) + 16;
        }
    }
}
