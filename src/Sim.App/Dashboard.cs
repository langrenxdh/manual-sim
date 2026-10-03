using System.Numerics;
using Raylib_cs;
using Sim.Core;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>
/// Golf-style instrument strip below the road view: tachometer, speedometer, gear, warning lights.
/// Display only; reads one <see cref="Frame"/>.
/// </summary>
public static class Dashboard
{
    // Gauge sweep in raylib degrees (0 = right, clockwise): bottom-left round over the top to bottom-right.
    private const float SweepStartDeg = 135;
    private const float SweepDeg = 270;
    private const double TachMaxRpm = 7000;
    private const double SpeedoMaxKmh = 220;

    private static readonly Color PanelBg = new(18, 18, 22, 255);
    private static readonly Color Dial = new(225, 225, 230, 255);
    private static readonly Color Needle = new(255, 90, 40, 255);
    private static readonly Color Redline = new(200, 30, 30, 255);
    private static readonly Color Dim = new(70, 70, 78, 255);

    public static void Draw(in Frame f, VehicleParams p, Rectangle area)
    {
        BeginScissorMode((int)area.X, (int)area.Y, (int)area.Width, (int)area.Height);
        DrawInstruments(f, p, area);
        EndScissorMode();
    }

    private static void DrawInstruments(in Frame f, VehicleParams p, Rectangle r)
    {
        var s = f.State;
        DrawRectangleRec(r, PanelBg);
        float radius = Math.Min(r.Height * 0.42f, r.Width * 0.17f);
        var tachCentre = new Vector2(r.X + r.Width * 0.24f, r.Y + r.Height * 0.52f);
        var speedoCentre = new Vector2(r.X + r.Width * 0.76f, r.Y + r.Height * 0.52f);

        DrawGauge(tachCentre, radius, s.EngineRpm, TachMaxRpm, 1000, v => (v / 1000).ToString("F0"),
            p.Engine.RedlineRpm, "x1000 rpm");
        DrawGauge(speedoCentre, radius, Math.Abs(s.SpeedKmh), SpeedoMaxKmh, 20, v => v.ToString("F0"),
            null, "km/h");

        // Centre column: gear, warning lights, wheel status.
        float cx = r.X + r.Width * 0.5f;
        bool blinkOn = GetTime() % 0.5 < 0.25;
        string gear = GearText(s.EngagedGear);
        Color gearColour = Dial;
        if (s.Grinding)
        {
            gear = GearText(f.Input.Input.Shifter);
            gearColour = blinkOn ? Redline : Dim;
        }
        Ui.Centred(gear, cx, r.Y + r.Height * 0.12f, (int)(r.Height * 0.38f), gearColour);
        if (s.Grinding) Ui.Centred("GRIND", cx, r.Y + r.Height * 0.52f, 28, Redline);

        bool engineOff = !s.Firing && s.EngineRpm < p.Engine.StallRpm;
        Lights(cx, r.Y + r.Height * 0.66f,
            ("ENGINE", engineOff, Redline),
            ("(P)", f.Input.Input.Handbrake, Redline),
            ("HOLD", s.HillHold != HillHoldState.Inactive, new Color(60, 200, 90, 255)));

        string status = f.InputError != null ? $"input error: {f.InputError}"
            : !f.Input.Connected ? "NO WHEEL - plug in the G29 (PS3 mode)"
            : !f.Input.PedalsReported ? "press each pedal once so the wheel reports them"
            : engineOff ? "engine off - hold X to start"
            : "";
        if (status.Length > 0) Ui.Centred(status, cx, r.Y + r.Height * 0.84f, 22, blinkOn ? Needle : Dial);
    }

    private static void DrawGauge(Vector2 c, float radius, double value, double max, double majorStep,
        Func<double, string> label, double? redFrom, string unit)
    {
        float Angle(double v) => SweepStartDeg + (float)(Math.Clamp(v / max, 0, 1) * SweepDeg);
        Vector2 At(float deg, float rr) => c + rr * new Vector2(MathF.Cos(deg * MathF.PI / 180), MathF.Sin(deg * MathF.PI / 180));

        DrawRing(c, radius * 0.97f, radius, SweepStartDeg, SweepStartDeg + SweepDeg, 64, Dim);
        if (redFrom is double red)
            DrawRing(c, radius * 0.86f, radius * 0.97f, Angle(red), SweepStartDeg + SweepDeg, 32, Redline);

        int fontSize = Math.Max(12, (int)(radius * 0.14f));
        for (double v = 0; v <= max + 1e-9; v += majorStep / 2)
        {
            bool major = Math.Abs(v / majorStep - Math.Round(v / majorStep)) < 1e-9;
            float a = Angle(v);
            DrawLineEx(At(a, radius * (major ? 0.82f : 0.88f)), At(a, radius * 0.96f), major ? 3 : 1.5f, Dial);
            if (major) Ui.Centred(label(v), At(a, radius * 0.68f).X, At(a, radius * 0.68f).Y - fontSize / 2f, fontSize, Dial);
        }
        Ui.Centred(unit, c.X, c.Y + radius * 0.35f, Math.Max(10, fontSize * 2 / 3), Dim);

        DrawLineEx(c, At(Angle(value), radius * 0.88f), 4, Needle);
        DrawCircleV(c, radius * 0.06f, Needle);
    }

    private const float LightTextSize = 26;
    private const float LightGap = 24;

    /// <summary>Warning lights in one row centred on cx; lit ones in their colour, others dim.</summary>
    private static void Lights(float cx, float y, params (string Text, bool On, Color Colour)[] lights)
    {
        float total = lights.Sum(l => Ui.Width(l.Text, LightTextSize)) + LightGap * (lights.Length - 1);
        float x = cx - total / 2;
        foreach (var (text, on, colour) in lights)
        {
            Ui.Text(text, x, y, LightTextSize, on ? colour : Dim);
            x += Ui.Width(text, LightTextSize) + LightGap;
        }
    }

    private static string GearText(Gear g) => g switch
    {
        Gear.Neutral => "N",
        Gear.Reverse => "R",
        _ => ((int)g).ToString(),
    };
}
