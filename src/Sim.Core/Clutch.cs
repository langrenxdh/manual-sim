namespace Sim.Core;

/// <summary>Clutch pedal to engagement mapping. Public so the teaching overlay draws the same curve the physics uses.</summary>
public static class Clutch
{
    /// <summary>Maps clutch pedal depression (0 = up, 1 = floor) to engagement c (0..1).</summary>
    public static double Engagement(ClutchParams p, double pedal)
    {
        double x = (p.BitePoint - pedal) / p.BiteZoneWidth;
        x = Math.Clamp(x, 0, 1);
        return Math.Pow(x, p.EngagementExponent);
    }
}
