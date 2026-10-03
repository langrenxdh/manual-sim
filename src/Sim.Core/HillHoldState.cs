namespace Sim.Core;

/// <summary>States of the hill-start assist (see the state diagram in docs/design.md).</summary>
public enum HillHoldState
{
    Inactive,
    /// <summary>On a slope, stationary, brake pressed: pressure will be retained on release.</summary>
    Armed,
    /// <summary>Brake released, retaining pressure while the hold timer runs.</summary>
    Holding,
    /// <summary>Drive torque overcame the slope; pressure ramps down, car drives away.</summary>
    ReleasingDriveAway,
    /// <summary>Hold timer expired without enough drive torque; pressure ramps down, car rolls back.</summary>
    ReleasingRollback,
}
