namespace Sim.Input;

/// <summary>
/// Turns one raw pedal axis into a 0..1 value: normalise, apply dead zones, then low-pass filter.
/// The G29 pedals are 8-bit (256 levels); without the filter each level step would reach the
/// physics as a torque step and feel like a fake judder.
/// </summary>
public sealed class PedalChannel
{
    private bool _primed;

    /// <summary>Normalised value before the dead zones and filter (0 = released, 1 = floored).</summary>
    public double Normalised { get; private set; }
    /// <summary>Value after dead zones and filter: what the physics sees.</summary>
    public double Value { get; private set; }

    /// <param name="raw">SDL axis value, -32768..32767.</param>
    /// <param name="dtS">Time since the previous sample.</param>
    public double Update(PedalConfig c, short raw, double dtS)
    {
        double n = (raw + 32768) / 65535.0;
        if (c.Inverted) n = 1 - n;
        Normalised = n;

        double travel = 1 - c.DeadZoneReleased - c.DeadZonePressed;
        double target = Math.Clamp((n - c.DeadZoneReleased) / travel, 0, 1);

        if (!_primed)
        {
            Value = target;
            _primed = true;
        }
        else
        {
            double rc = 1 / (2 * Math.PI * c.LowPassCutoffHz);
            Value += (target - Value) * dtS / (rc + dtS);
        }
        return Value;
    }

    /// <summary>Forgets the filter state, e.g. after the device reconnects.</summary>
    public void Reset()
    {
        _primed = false;
        Normalised = Value = 0;
    }
}
