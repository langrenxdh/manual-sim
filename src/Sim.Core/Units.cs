namespace Sim.Core;

/// <summary>Unit conversions. Internally the core works in SI (rad/s, m/s, N, Nm).</summary>
public static class Units
{
    public const double RadPerSecPerRpm = 2.0 * Math.PI / 60.0;
    public const double MpsPerKmh = 1.0 / 3.6;

    public static double RpmToRadPerSec(double rpm) => rpm * RadPerSecPerRpm;
    public static double RadPerSecToRpm(double radPerSec) => radPerSec / RadPerSecPerRpm;
    public static double KmhToMps(double kmh) => kmh * MpsPerKmh;
    public static double MpsToKmh(double mps) => mps / MpsPerKmh;
}
