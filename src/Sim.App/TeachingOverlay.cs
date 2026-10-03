using System.Numerics;
using Raylib_cs;
using Sim.Core;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>
/// Teaching-mode overlays (design doc table: immersion vs teaching), drawn at the left and right
/// edges of the road view so the line of sight stays clear. Left: the clutch curve with the bite
/// zone and where the foot is. Right: distance from stalling, idle-control reserve, hill hold.
/// </summary>
public static class TeachingOverlay
{
    private const float Width = 260, Pad = 14, EdgeMargin = 16;

    /// <summary>Horizontal space each side box takes from the road view (box plus both margins).</summary>
    public const float SideReserve = Width + 2 * EdgeMargin;

    /// <summary>Narrower than this between the boxes, they stack on the left instead.</summary>
    private const float MinCentreWidth = 420;
    private const float ClutchBoxHeight = 250, EngineBoxHeight = 194, StackGap = 12;

    private static bool Stacked(Rectangle road) => road.Width - 2 * SideReserve < MinCentreWidth;

    /// <summary>
    /// The part of the road view clear of the side boxes, for other overlays. When the view is too
    /// narrow the boxes stack on the left and the whole view is returned (other overlays may cover them).
    /// </summary>
    public static Rectangle Centre(Rectangle road) => Stacked(road)
        ? road
        : new(road.X + SideReserve, road.Y, road.Width - 2 * SideReserve, road.Height);

    private const int CurveSamples = 60;

    private static readonly Color Bg = new(10, 10, 14, 200);
    private static readonly Color Fg = new(225, 225, 232, 255);
    private static readonly Color Muted = new(140, 140, 150, 255);
    private static readonly Color BiteZone = new(255, 170, 40, 60);
    private static readonly Color Curve = new(255, 170, 40, 255);
    private static readonly Color Foot = new(255, 255, 255, 255);
    private static readonly Color Good = new(90, 210, 110, 255);
    private static readonly Color Warn = new(240, 200, 60, 255);
    private static readonly Color Bad = new(235, 70, 60, 255);

    public static void Draw(in Frame f, VehicleParams p, Rectangle road)
    {
        float top = road.Y + road.Height * 0.18f;
        DrawClutch(f, p.Clutch, new Rectangle(road.X + EdgeMargin, top, Width, ClutchBoxHeight));
        var engine = Stacked(road)
            ? new Rectangle(road.X + EdgeMargin, top + ClutchBoxHeight + StackGap, Width, EngineBoxHeight)
            : new Rectangle(road.X + road.Width - Width - EdgeMargin, top, Width, EngineBoxHeight);
        DrawEngine(f.State, p, engine);
    }

    private static void DrawClutch(in Frame f, ClutchParams c, Rectangle r)
    {
        DrawRectangleRounded(r, 0.06f, 6, Bg);
        var s = f.State;
        Ui.Text("Clutch", r.X + Pad, r.Y + 8, 22, Fg);
        string status = s.ClutchLocked ? "locked" : $"slipping {Math.Abs(s.ClutchSlipRpm):F0} rpm";
        Ui.Text(status, r.X + Pad, r.Y + 34, 17, s.ClutchLocked ? Good : Warn);

        // Plot: x = pedal travel (0 = released, 1 = floor), y = engagement c.
        var plot = new Rectangle(r.X + Pad, r.Y + 64, r.Width - 2 * Pad, r.Height - 64 - 30);
        float X(double pedal) => plot.X + (float)pedal * plot.Width;
        float Y(double engagement) => plot.Y + plot.Height - (float)engagement * plot.Height;

        double zoneStart = Math.Max(0, c.BitePoint - c.BiteZoneWidth), zoneEnd = c.BitePoint;
        DrawRectangleRec(new Rectangle(X(zoneStart), plot.Y, X(zoneEnd) - X(zoneStart), plot.Height), BiteZone);
        DrawRectangleLinesEx(plot, 1, Muted);

        Vector2? previous = null;
        for (int i = 0; i <= CurveSamples; i++)
        {
            double pedal = (double)i / CurveSamples;
            var point = new Vector2(X(pedal), Y(Clutch.Engagement(c, pedal)));
            if (previous is { } prev) DrawLineEx(prev, point, 2, Curve);
            previous = point;
        }

        double foot = f.Input.Input.Clutch;
        DrawCircleV(new Vector2(X(foot), Y(s.ClutchEngagement)), 7, Foot);
        DrawLineV(new Vector2(X(foot), plot.Y), new Vector2(X(foot), plot.Y + plot.Height), new Color(255, 255, 255, 90));

        Ui.Text("up", plot.X, plot.Y + plot.Height + 4, 15, Muted);
        Ui.Centred("bite zone", (X(zoneStart) + X(zoneEnd)) / 2, plot.Y + plot.Height + 4, 15, Curve);
        Ui.Text("floor", plot.X + plot.Width - Ui.Width("floor", 15), plot.Y + plot.Height + 4, 15, Muted);
    }

    private static void DrawEngine(in SimState s, VehicleParams p, Rectangle r)
    {
        DrawRectangleRounded(r, 0.06f, 6, Bg);
        float x = r.X + Pad, w = r.Width - 2 * Pad, y = r.Y + 8;

        // Distance from stalling, scaled so a full bar is the margin at idle.
        double idleMargin = p.Engine.IdleRpm - p.Engine.StallRpm;
        double margin = Math.Clamp(s.StallMarginRpm / idleMargin, 0, 1);
        Ui.Text("Stall margin", x, y, 20, Fg);
        Ui.Text(s.StallMarginRpm >= 0 ? $"{s.StallMarginRpm:F0} rpm" : "stalled", x + w - 90, y + 2, 17, Fg);
        Bar(x, y + 28, w, margin, margin > 0.5 ? Good : margin > 0.2 ? Warn : Bad);

        // How much of the idle controller's torque is in use: near 100 % it can no longer hold idle.
        y += 64;
        double usage = Math.Clamp(s.IdleControlUsage, 0, 1);
        Ui.Text("Idle control", x, y, 20, Fg);
        Ui.Text($"{usage * 100:F0} %", x + w - 50, y + 2, 17, Fg);
        Bar(x, y + 28, w, usage, usage < 0.6 ? Good : usage < 0.9 ? Warn : Bad);

        // Hill-start assist state and remaining hold time.
        y += 64;
        Ui.Text("Hill hold", x, y, 20, Fg);
        string state = s.HillHold switch
        {
            HillHoldState.Inactive => "off",
            HillHoldState.Armed => "armed",
            HillHoldState.Holding => $"holding {s.HillHoldRemainingS:F1} s",
            HillHoldState.ReleasingDriveAway => "releasing",
            HillHoldState.ReleasingRollback => "rolling back",
            _ => s.HillHold.ToString(),
        };
        Ui.Text(state, x + w - Ui.Width(state, 17), y + 2, 17, s.HillHold == HillHoldState.ReleasingRollback ? Bad : Fg);
        double hold = s.HillHold == HillHoldState.Holding ? s.HillHoldRemainingS / p.HillHold.HoldTimeS : 0;
        Bar(x, y + 28, w, Math.Clamp(hold, 0, 1), Warn);
    }

    private static void Bar(float x, float y, float w, double fraction, Color colour)
    {
        DrawRectangle((int)x, (int)y, (int)w, 14, new Color(50, 50, 58, 255));
        DrawRectangle((int)x, (int)y, (int)(w * fraction), 14, colour);
    }
}
