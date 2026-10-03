namespace Sim.Core;

/// <summary>Complete observable model state after a physics step.</summary>
public readonly record struct SimState
{
    public double TimeS { get; init; }

    // Engine
    public double EngineRpm { get; init; }
    /// <summary>Turbo boost state B (0..1).</summary>
    public double Boost { get; init; }
    /// <summary>Effective throttle u = max(pedal, idle controller).</summary>
    public double Throttle { get; init; }
    /// <summary>Gross combustion torque the idle controller requested.</summary>
    public double IdleControlTorqueNm { get; init; }
    /// <summary>Idle controller output as a fraction of its limit (1 = no reserve left).</summary>
    public double IdleControlUsage { get; init; }
    /// <summary>Combustion torque including the firing pulsation.</summary>
    public double CombustionTorqueNm { get; init; }
    public double FrictionTorqueNm { get; init; }
    /// <summary>Engine speed above the stall threshold; negative once stalled.</summary>
    public double StallMarginRpm { get; init; }
    /// <summary>True when combustion is possible (above stall speed, below fuel cut).</summary>
    public bool Firing { get; init; }
    /// <summary>Shudder intensity (0..1), drives sound, force feedback and camera shake.</summary>
    public double ShudderIntensity { get; init; }
    /// <summary>Firing pulsation phase in radians (two firings per crank revolution).</summary>
    public double FiringPhaseRad { get; init; }

    // Clutch
    /// <summary>Clutch engagement c (0..1).</summary>
    public double ClutchEngagement { get; init; }
    /// <summary>Torque from engine to gearbox (positive drives the gearbox forward).</summary>
    public double ClutchTorqueNm { get; init; }
    public bool ClutchLocked { get; init; }
    /// <summary>Gearbox input shaft speed.</summary>
    public double InputShaftRpm { get; init; }
    public double ClutchSlipRpm { get; init; }

    // Gearbox
    public Gear EngagedGear { get; init; }
    /// <summary>Lever is in a gear but the clutch is not pressed far enough: gears grind.</summary>
    public bool Grinding { get; init; }

    // Vehicle
    public double SpeedMps { get; init; }
    public double SpeedKmh => Units.MpsToKmh(SpeedMps);
    public double PositionM { get; init; }
    public double AccelerationMps2 { get; init; }
    public double Grade { get; init; }
    public double DriveForceN { get; init; }

    // Hill hold
    public HillHoldState HillHold { get; init; }
    public double HillHoldRemainingS { get; init; }
    public double HillHoldForceN { get; init; }

    // Temperatures and accessories (M7)
    public double ClutchTempC { get; init; }
    /// <summary>Clutch friction relative to a cool clutch (below 1 = fading).</summary>
    public double ClutchFrictionFactor { get; init; }
    /// <summary>Hot enough that a driver would smell the clutch (warning only).</summary>
    public bool ClutchSmell { get; init; }
    public double EngineTempC { get; init; }
    /// <summary>Idle speed the ECU is aiming for right now (higher when cold or with the air-con on).</summary>
    public double IdleTargetRpm { get; init; }
    public bool AirCon { get; init; }

    // Steering and pose (M9). Without steering: WorldX = PositionM, WorldY = 0, heading 0.
    public bool Steering { get; init; }
    public double WorldX { get; init; }
    public double WorldY { get; init; }
    /// <summary>Direction of travel, radians counter-clockwise from +X.</summary>
    public double HeadingRad { get; init; }
    public double YawRateRadPerS { get; init; }
    /// <summary>Positive to the left.</summary>
    public double LateralAccelMps2 { get; init; }
    public double RoadWheelAngleDeg { get; init; }
    /// <summary>The front tyres are past their grip limit (understeer).</summary>
    public bool FrontSliding { get; init; }
    /// <summary>Torque on the steering wheel from the tyres, Nm; positive turns the wheel to the left.</summary>
    public double AligningTorqueNm { get; init; }
}
