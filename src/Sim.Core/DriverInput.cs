namespace Sim.Core;

/// <summary>
/// Driver controls for one physics step. Pedals are already filtered and calibrated by the
/// input layer: 0 = released, 1 = fully pressed. Out-of-range values are clamped.
/// </summary>
public readonly record struct DriverInput(
    double Clutch,
    double Throttle,
    double Brake,
    Gear Shifter,
    bool Handbrake = false,
    bool Starter = false,
    bool AirCon = false);
