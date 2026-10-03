namespace Sim.Core;

/// <summary>
/// Integration of a body subject to an active load and Coulomb friction (Karnopp-style stiction):
/// friction opposes motion with a fixed capacity, holds the body at rest while the active load is
/// within that capacity, and can stop the body but never drive it backwards.
/// </summary>
internal static class Friction
{
    public static double Sign(double x) => x > 0 ? 1 : x < 0 ? -1 : 0;

    /// <param name="speed">Current speed (rad/s or m/s).</param>
    /// <param name="activeLoad">Sum of non-friction loads (Nm or N).</param>
    /// <param name="capacity">Friction capacity, &gt;= 0.</param>
    /// <param name="inertia">Inertia (kg m^2) or mass (kg).</param>
    /// <returns>Speed after one step.</returns>
    public static double Advance(double speed, double activeLoad, double capacity, double inertia, double dt)
    {
        if (speed == 0)
        {
            if (Math.Abs(activeLoad) <= capacity) return 0;
            return (activeLoad - capacity * Sign(activeLoad)) / inertia * dt;
        }

        double next = speed + (activeLoad - capacity * Sign(speed)) / inertia * dt;
        if (Sign(next) != Sign(speed) && Math.Abs(activeLoad) <= capacity) return 0;
        return next;
    }
}
