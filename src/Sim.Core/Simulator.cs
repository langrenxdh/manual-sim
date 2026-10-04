namespace Sim.Core;

/// <summary>
/// Longitudinal drivetrain model: engine, clutch, gearbox and vehicle, advanced with a fixed
/// 1 ms step. Driver input in, <see cref="SimState"/> out. No clocks, threads, IO or devices.
/// </summary>
/// <remarks>
/// Three bodies: engine (ω_e), gearbox input shaft (ω_t) and vehicle (v). With a gear engaged the
/// input shaft is rigidly tied to the wheels and the vehicle is handled as an inertia reflected to
/// the input shaft. The clutch couples engine and input shaft: it slips with torque c·T_max·sign(Δω)
/// or locks and transmits the torque needed to keep both at the same speed, up to its capacity.
/// Stalling, shudder, lugging and hill behaviour are not special-cased; they follow from these
/// equations and the parameters.
/// </remarks>
public sealed class Simulator
{
    /// <summary>Fixed physics step (docs/design.md: 1 kHz).</summary>
    public const double StepS = 0.001;

    private VehicleParams _p;
    private readonly Road _road;
    private readonly HillHold _hillHold = new();
    private Xorshift _rng;

    private long _steps;
    private double _we;          // engine speed, rad/s
    private double _wIn;         // input shaft speed, rad/s
    private double _v;           // vehicle speed, m/s
    private double _x;           // position along the road, m
    private double _boost;       // turbo boost state B
    private double _idleIntegral;
    private double _firingPhase; // rad, one cycle per firing (cylinders / 2 per crank revolution)
    private double _firingFactor = 1;
    private bool _clutchLocked;
    private Gear _gear;
    private bool _grinding;
    private double _driveForceN;
    private double _clutchTempC;  // M7: lumped clutch temperature
    private double _engineTempC;  // M7: lumped engine temperature
    private bool _airCon;
    private double _idleTargetRpm;
    // M9 lateral state (only moves when _steering)
    private readonly bool _steering;
    private double _worldX, _worldY, _heading, _yawRate, _lateralAccel, _roadWheelDeg, _aligningTorqueNm;
    private bool _frontSliding;

    /// <param name="engineTempC">Engine temperature at start; null = warm (operating temperature).</param>
    /// <param name="steering">
    /// M9: when true the steering-wheel input turns the car on a flat 2D plane (pose X, Y, heading); when
    /// false the car runs straight along the road, exactly as before steering existed.
    /// </param>
    /// <param name="headingRad">Initial heading for steering (0 = along +X).</param>
    public Simulator(VehicleParams parameters, Road road, Gear gear = Gear.Neutral,
        double speedMps = 0, double? engineRpm = null, double positionM = 0, double? engineTempC = null,
        bool steering = false, double startX = 0, double startY = 0, double headingRad = 0)
    {
        _steering = steering;
        _worldX = steering ? startX : positionM;
        _worldY = steering ? startY : 0;
        _heading = steering ? headingRad : 0;
        parameters.Validate();
        _p = parameters;
        _road = road;
        _rng = new Xorshift(parameters.Shudder.Seed);
        _gear = gear;
        _v = speedMps;
        _x = positionM;
        _engineTempC = engineTempC ?? parameters.EngineThermal.WarmC;
        _clutchTempC = parameters.Environment.AmbientTempC;
        _idleTargetRpm = parameters.Engine.IdleRpm + parameters.EngineThermal.ColdIdleExtraRpm * ColdFactor(parameters);

        double matchedRpm = gear == Gear.Neutral ? 0 : Units.RadPerSecToRpm(InputShaftPerMetre(gear) * speedMps);
        bool rolling = gear != Gear.Neutral && speedMps != 0;
        double rpm = engineRpm ?? (rolling ? matchedRpm : parameters.Engine.IdleRpm);
        _we = Units.RpmToRadPerSec(rpm);
        _wIn = gear == Gear.Neutral ? _we : InputShaftPerMetre(gear) * speedMps;
        _clutchLocked = _we == _wIn;

        // Start in steady state: idle controller already supplying the idle friction torque.
        if (Math.Abs(rpm - parameters.Engine.IdleRpm) < 1e-9)
            _idleIntegral = Math.Min(EngineFriction(parameters, rpm, airCon: false), parameters.IdleControl.MaxTorqueNm);
        _boost = 0;

        State = BuildState(road.GradeAt(positionM), 0, 0, 0, 0, 0, 0, 0, 0, false, 0);
    }

    /// <summary>Parameters in use. The tuning panel may replace them between steps.</summary>
    public VehicleParams Params
    {
        get => _p;
        set
        {
            value.Validate();
            _p = value;
        }
    }

    public SimState State { get; private set; }

    public void Step(in DriverInput input)
    {
        var p = _p;
        const double dt = StepS;
        double clutchPedal = Math.Clamp(input.Clutch, 0, 1);
        double throttlePedal = Math.Clamp(input.Throttle, 0, 1);
        double brakePedal = Math.Clamp(input.Brake, 0, 1);

        double engagement = Clutch.Engagement(p.Clutch, clutchPedal);
        UpdateGearbox(p, input.Shifter, engagement);

        // Road and hill hold
        double grade = _road.GradeAt(_x);
        double slope = Math.Atan(grade);
        double mass = p.Chassis.MassKg;
        double gravity = p.Environment.GravityMps2;
        double gravityN = -mass * gravity * Math.Sin(slope);
        _hillHold.Update(p.HillHold, brakePedal, _v, grade, _driveForceN, gravityN, dt);

        // Engine torque: naturally aspirated part + boost part, throttle = max(pedal, idle controller)
        var e = p.Engine;
        double rpm = Units.RadPerSecToRpm(_we);
        bool firing = rpm >= e.StallRpm && rpm < e.FuelCutRpm;
        double fullTorque = firing ? e.NaTorqueNm.Evaluate(rpm) + _boost * e.BoostTorqueNm.Evaluate(rpm) : 0;

        var ic = p.IdleControl;
        // The ECU idles higher when the engine is cold and when the air-con compressor is running.
        _airCon = input.AirCon;
        _idleTargetRpm = e.IdleRpm + p.EngineThermal.ColdIdleExtraRpm * ColdFactor(p)
                         + (input.AirCon ? p.AirCon.IdleBumpRpm : 0);
        double idleErrorRpm = _idleTargetRpm - rpm;
        _idleIntegral = Math.Clamp(_idleIntegral + ic.IntegralGainNmPerRpmS * idleErrorRpm * dt, 0, ic.MaxTorqueNm);
        double idleTorque = Math.Clamp(ic.ProportionalGainNmPerRpm * idleErrorRpm + _idleIntegral, 0, ic.MaxTorqueNm);
        double idleThrottle = fullTorque > 0 ? Math.Min(1, idleTorque / fullTorque) : 0;
        double throttle = Math.Max(throttlePedal, idleThrottle);
        double combustion = throttle * fullTorque;

        double boostTarget = firing ? throttle * e.BoostTarget.Evaluate(rpm) : 0;
        _boost += (boostTarget - _boost) * dt / e.BoostTimeConstantS;

        // Firing pulsation: grows with load (via combustion torque and throttle) and at low speed.
        var s = p.Shudder;
        double lowSpeed = firing ? Math.Clamp((s.FadeOutRpm - rpm) / (s.FadeOutRpm - e.StallRpm), 0, 1) : 0;
        double shudderIntensity = throttle * lowSpeed;
        AdvanceFiringPhase(s, FiringsPerRev(p), dt);
        double pulsation = combustion * s.MaxAmplitudeRatio * lowSpeed * _firingFactor * Math.Sin(_firingPhase);

        double starter = input.Starter
            ? e.StarterStallTorqueNm * Math.Clamp(1 - rpm / e.StarterFreeRpm, 0, 1)
            : 0;

        double engineActive = combustion + pulsation + starter;
        double engineFriction = EngineFriction(p, rpm, input.AirCon);

        // Vehicle loads
        var ch = p.Chassis;
        double aeroN = -0.5 * p.Environment.AirDensityKgM3 * ch.DragAreaM2 * _v * Math.Abs(_v);
        double externalN = gravityN + aeroN;
        double brakeN = Math.Max(brakePedal * ch.MaxBrakeForceN, _hillHold.ForceN)
            + (input.Handbrake ? ch.HandbrakeForceN : 0);
        double vehicleFrictionN = ch.RollingResistanceCoeff * mass * gravity * Math.Cos(slope) + brakeN;

        double clutchCapacity = engagement * p.Clutch.MaxTorqueNm * ClutchFrictionFactor(p);
        var gb = p.Gearbox;
        double previousSpeed = _v;
        double clutchTorque;

        if (_gear == Gear.Neutral)
        {
            clutchTorque = SolveClutch(e.InertiaKgM2, engineActive, engineFriction,
                gb.InputShaftInertiaKgM2, 0, gb.InputShaftDragNm, clutchCapacity, dt);
            _v = Friction.Advance(_v, externalN, vehicleFrictionN, mass, dt);
            _driveForceN = 0;
        }
        else
        {
            // Vehicle reflected to the input shaft: ω_t = k·v, F_drive = T·k·η.
            double k = InputShaftPerMetre(_gear);
            double eta = gb.Efficiency;
            double reflectedInertia = gb.InputShaftInertiaKgM2 + mass / (k * k * eta);
            double reflectedLoad = externalN / (k * eta);
            double reflectedFriction = vehicleFrictionN / (Math.Abs(k) * eta) + gb.InputShaftDragNm;
            _wIn = k * _v;
            clutchTorque = SolveClutch(e.InertiaKgM2, engineActive, engineFriction,
                reflectedInertia, reflectedLoad, reflectedFriction, clutchCapacity, dt);
            _v = _wIn / k;
            _driveForceN = clutchTorque * k * eta;
        }

        UpdateTemperatures(p, clutchTorque, combustion, dt);
        _x += _v * dt;
        if (_steering) UpdateLateral(p, input.SteeringWheelDeg, dt);
        else _worldX = _x;
        _steps++;

        double idleUsage = ic.MaxTorqueNm > 0 ? idleTorque / ic.MaxTorqueNm : 1;
        State = BuildState(grade, throttle, idleTorque, idleUsage, combustion + pulsation, engineFriction,
            shudderIntensity, engagement, clutchTorque, firing, (_v - previousSpeed) / dt);
    }

    /// <summary>0 when warm, rising linearly to 1 at the cold reference temperature.</summary>
    private double ColdFactor(VehicleParams p)
    {
        var t = p.EngineThermal;
        return Math.Clamp((t.WarmC - _engineTempC) / (t.WarmC - t.ColdReferenceC), 0, 1);
    }

    /// <summary>Internal friction: the warm curve, raised when cold, plus the air-con compressor load.</summary>
    private double EngineFriction(VehicleParams p, double rpm, bool airCon) =>
        p.Engine.FrictionTorqueNm.Evaluate(Math.Abs(rpm)) * (1 + p.EngineThermal.ColdFrictionExtra * ColdFactor(p))
        + (airCon ? p.AirCon.LoadNm : 0);

    /// <summary>Clutch friction coefficient relative to cold: 1 until fade starts, falling to the minimum.</summary>
    private double ClutchFrictionFactor(VehicleParams p)
    {
        var t = p.ClutchThermal;
        double fade = Math.Clamp((_clutchTempC - t.FadeStartC) / (t.FadeEndC - t.FadeStartC), 0, 1);
        return 1 - fade * (1 - t.MinFrictionFactor);
    }

    /// <summary>
    /// Clutch: slip power (transmitted torque x slip speed) heats it; it cools towards ambient.
    /// Engine: waste heat in proportion to mechanical combustion power warms it; it cools towards
    /// ambient, and the thermostat adds strong cooling above operating temperature.
    /// </summary>
    private void UpdateTemperatures(VehicleParams p, double clutchTorque, double combustionTorque, double dt)
    {
        double ambient = p.Environment.AmbientTempC;
        var c = p.ClutchThermal;
        double slipW = _clutchLocked ? 0 : Math.Abs(clutchTorque * (_we - _wIn));
        _clutchTempC += (slipW - c.CoolingWPerK * (_clutchTempC - ambient)) / c.HeatCapacityJPerK * dt;

        var e = p.EngineThermal;
        double heatW = e.HeatPerMechanicalW * Math.Max(0, combustionTorque * _we);
        double coolW = e.CoolingWPerK * (_engineTempC - ambient) + e.ThermostatWPerK * Math.Max(0, _engineTempC - e.WarmC);
        _engineTempC += (heatW - coolW) / e.HeatCapacityJPerK * dt;
    }

    /// <summary>
    /// Kinematic single-track step (M9b). The road-wheel angle sets the path curvature; lateral
    /// acceleration is capped by tyre grip (understeer beyond it). The front lateral force times the
    /// trail gives the aligning torque; the pneumatic trail fades as the front tyres slide. Steering
    /// geometry adds a centring torque from the road-wheel angle while rolling.
    /// </summary>
    private void UpdateLateral(VehicleParams p, double steeringWheelDeg, double dt)
    {
        var st = p.Steering;
        double maxRoad = st.MaxRoadWheelAngleDeg;
        _roadWheelDeg = Math.Clamp(steeringWheelDeg / st.SteeringRatio, -maxRoad, maxRoad);
        double curvature = Math.Tan(_roadWheelDeg * Math.PI / 180) / st.WheelbaseM;
        double demand = _v * _v * curvature;                       // lateral acceleration the wheels ask for
        double limit = st.TyreGripMu * p.Environment.GravityMps2;
        _frontSliding = Math.Abs(demand) > limit;
        _lateralAccel = _frontSliding ? Math.Sign(demand) * limit : demand;
        _yawRate = _frontSliding ? _lateralAccel / _v : _v * curvature;

        _heading += _yawRate * dt;
        _worldX += _v * Math.Cos(_heading) * dt;
        _worldY += _v * Math.Sin(_heading) * dt;

        double frontShare = (st.WheelbaseM - st.CgToFrontAxleM) / st.WheelbaseM;
        double frontLateralN = frontShare * p.Chassis.MassKg * _lateralAccel;
        double excess = Math.Max(0, Math.Abs(demand) / limit - 1);
        double trail = st.MechanicalTrailM + st.PneumaticTrailM * Math.Clamp(1 - excess / st.PneumaticTrailFadeRatio, 0, 1);
        double tyreNm = frontLateralN * trail * Math.Sign(_v == 0 ? 1 : _v);
        // Caster and kingpin inclination lift the front as the wheels turn; rolling lets them settle back.
        double frontLoadN = frontShare * p.Chassis.MassKg * p.Environment.GravityMps2;
        double rolling = Math.Clamp(Math.Abs(_v) / st.CentringFullSpeedMps, 0, 1);
        double centringNm = frontLoadN * st.CentringLeverM * Math.Sin(_roadWheelDeg * Math.PI / 180) * rolling;
        // Pushes the wheel back towards straight: opposite in sign to a forward turn.
        _aligningTorqueNm = -st.PowerAssistFactor * (tyreNm + centringNm) / st.SteeringRatio;
    }

    /// <summary>Gearbox input shaft rad/s per m/s of vehicle speed for a gear (negative in reverse).</summary>
    private double InputShaftPerMetre(Gear gear)
    {
        var gb = _p.Gearbox;
        double ratio = gear switch
        {
            Gear.Reverse => -gb.ReverseRatio,
            Gear.Neutral => throw new InvalidOperationException("No ratio in neutral."),
            _ => gb.ForwardRatios[(int)gear - 1],
        };
        double wheelRadius = _p.Chassis.TyreCircumferenceM / (2 * Math.PI);
        return ratio * gb.FinalDriveRatio / wheelRadius;
    }

    /// <summary>Shift rules 1 and 2 of docs/design.md. Rule 3 (after the clutch is released) is physics.</summary>
    private void UpdateGearbox(VehicleParams p, Gear shifter, double engagement)
    {
        if (shifter == Gear.Neutral)
        {
            _gear = Gear.Neutral;
            _grinding = false;
        }
        else if (shifter == _gear)
        {
            _grinding = false;
        }
        else if (engagement <= p.Clutch.ShiftMaxEngagement)
        {
            // Synchroniser brings the input shaft to wheel speed; the clutch then sees the slip.
            _gear = shifter;
            _grinding = false;
            _clutchLocked = false;
            _wIn = InputShaftPerMetre(shifter) * _v;
        }
        else
        {
            _gear = Gear.Neutral;
            _grinding = true;
        }
    }

    private static double FiringsPerRev(VehicleParams p) => p.Engine.Cylinders / 2.0;

    private void AdvanceFiringPhase(ShudderParams s, double firingsPerRev, double dt)
    {
        const double cycle = 2 * Math.PI;
        _firingPhase += firingsPerRev * _we * dt;
        if (_firingPhase >= cycle || _firingPhase < 0)
        {
            _firingPhase -= cycle * Math.Floor(_firingPhase / cycle);
            _firingFactor = 1 + s.Irregularity * _rng.NextSigned();
        }
    }

    /// <summary>
    /// Advances engine (ω_e) and input shaft (ω_t = _wIn) through the clutch. Returns the clutch
    /// torque from engine to gearbox.
    /// </summary>
    private double SolveClutch(double je, double engineActive, double engineFriction,
        double jt, double gearboxActive, double gearboxFriction, double capacity, double dt)
    {
        if (capacity <= 0)
        {
            _clutchLocked = false;
            _we = Friction.Advance(_we, engineActive, engineFriction, je, dt);
            _wIn = Friction.Advance(_wIn, gearboxActive, gearboxFriction, jt, dt);
            return 0;
        }

        if (_clutchLocked || _we == _wIn)
        {
            double w = _we;
            double next = Friction.Advance(w, engineActive + gearboxActive, engineFriction + gearboxFriction, je + jt, dt);
            double required = LockedClutchTorque(w, next, je, engineActive, engineFriction,
                gearboxActive, gearboxFriction, dt);
            if (Math.Abs(required) <= capacity)
            {
                _clutchLocked = true;
                _we = _wIn = next;
                return required;
            }
            _clutchLocked = false;
            double breakaway = capacity * Friction.Sign(required);
            _we = Friction.Advance(_we, engineActive - breakaway, engineFriction, je, dt);
            _wIn = Friction.Advance(_wIn, gearboxActive + breakaway, gearboxFriction, jt, dt);
            return breakaway;
        }

        double slip = _we - _wIn;
        double torque = capacity * Friction.Sign(slip);
        double nextEngine = Friction.Advance(_we, engineActive - torque, engineFriction, je, dt);
        double nextGearbox = Friction.Advance(_wIn, gearboxActive + torque, gearboxFriction, jt, dt);
        if (Friction.Sign(nextEngine - nextGearbox) != Friction.Sign(slip))
        {
            // Slip speed crossed zero within the step: the plates grab, momentum is shared.
            _we = _wIn = (je * nextEngine + jt * nextGearbox) / (je + jt);
            _clutchLocked = true;
        }
        else
        {
            _we = nextEngine;
            _wIn = nextGearbox;
        }
        return torque;
    }

    /// <summary>Torque the clutch must carry so that engine and input shaft move together.</summary>
    private static double LockedClutchTorque(double w, double next, double je, double engineActive,
        double engineFriction, double gearboxActive, double gearboxFriction, double dt)
    {
        if (next != 0)
        {
            // Moving: each side's friction acts at full capacity against the motion.
            double direction = Friction.Sign(next);
            double acceleration = (next - w) / dt;
            return engineActive - engineFriction * direction - je * acceleration;
        }

        // At rest: the friction split is indeterminate; pick the one needing least clutch torque.
        double total = engineActive + gearboxActive;
        double lo = Math.Max(-gearboxFriction, -total - engineFriction);
        double hi = Math.Min(gearboxFriction, -total + engineFriction);
        double gearboxFrictionActual = Math.Clamp(-gearboxActive, lo, hi);
        return -(gearboxActive + gearboxFrictionActual);
    }

    private SimState BuildState(double grade, double throttle, double idleTorque, double idleUsage,
        double combustion, double friction, double shudder, double engagement, double clutchTorque,
        bool firing, double acceleration)
    {
        double rpm = Units.RadPerSecToRpm(_we);
        double inputRpm = Units.RadPerSecToRpm(_wIn);
        return new SimState
        {
            TimeS = _steps * StepS,
            EngineRpm = rpm,
            Boost = _boost,
            Throttle = throttle,
            IdleControlTorqueNm = idleTorque,
            IdleControlUsage = idleUsage,
            CombustionTorqueNm = combustion,
            FrictionTorqueNm = friction,
            StallMarginRpm = rpm - _p.Engine.StallRpm,
            Firing = firing,
            ShudderIntensity = shudder,
            FiringPhaseRad = _firingPhase,
            FiringsPerRev = FiringsPerRev(_p),
            ClutchEngagement = engagement,
            ClutchTorqueNm = clutchTorque,
            ClutchLocked = _clutchLocked,
            InputShaftRpm = inputRpm,
            ClutchSlipRpm = rpm - inputRpm,
            EngagedGear = _gear,
            Grinding = _grinding,
            SpeedMps = _v,
            PositionM = _x,
            AccelerationMps2 = acceleration,
            Grade = grade,
            DriveForceN = _driveForceN,
            HillHold = _hillHold.State,
            HillHoldRemainingS = _hillHold.RemainingS,
            HillHoldForceN = _hillHold.ForceN,
            ClutchTempC = _clutchTempC,
            ClutchFrictionFactor = ClutchFrictionFactor(_p),
            ClutchSmell = _clutchTempC > _p.ClutchThermal.SmellAboveC,
            EngineTempC = _engineTempC,
            IdleTargetRpm = _idleTargetRpm,
            AirCon = _airCon,
            Steering = _steering,
            WorldX = _worldX,
            WorldY = _worldY,
            HeadingRad = _heading,
            YawRateRadPerS = _yawRate,
            LateralAccelMps2 = _lateralAccel,
            RoadWheelAngleDeg = _roadWheelDeg,
            FrontSliding = _frontSliding,
            AligningTorqueNm = _aligningTorqueNm,
        };
    }
}
