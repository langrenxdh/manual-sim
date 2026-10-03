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
    private double _firingPhase; // rad, two firings per crank revolution
    private double _firingFactor = 1;
    private bool _clutchLocked;
    private Gear _gear;
    private bool _grinding;
    private double _driveForceN;

    public Simulator(VehicleParams parameters, Road road, Gear gear = Gear.Neutral,
        double speedMps = 0, double? engineRpm = null, double positionM = 0)
    {
        parameters.Validate();
        _p = parameters;
        _road = road;
        _rng = new Xorshift(parameters.Shudder.Seed);
        _gear = gear;
        _v = speedMps;
        _x = positionM;

        double matchedRpm = gear == Gear.Neutral ? 0 : Units.RadPerSecToRpm(InputShaftPerMetre(gear) * speedMps);
        bool rolling = gear != Gear.Neutral && speedMps != 0;
        double rpm = engineRpm ?? (rolling ? matchedRpm : parameters.Engine.IdleRpm);
        _we = Units.RpmToRadPerSec(rpm);
        _wIn = gear == Gear.Neutral ? _we : InputShaftPerMetre(gear) * speedMps;
        _clutchLocked = _we == _wIn;

        // Start in steady state: idle controller already supplying the idle friction torque.
        if (Math.Abs(rpm - parameters.Engine.IdleRpm) < 1e-9)
            _idleIntegral = Math.Min(parameters.Engine.FrictionTorqueNm.Evaluate(rpm), parameters.IdleControl.MaxTorqueNm);
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
        double idleErrorRpm = e.IdleRpm - rpm;
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
        AdvanceFiringPhase(s, dt);
        double pulsation = combustion * s.MaxAmplitudeRatio * lowSpeed * _firingFactor * Math.Sin(_firingPhase);

        double starter = input.Starter
            ? e.StarterStallTorqueNm * Math.Clamp(1 - rpm / e.StarterFreeRpm, 0, 1)
            : 0;

        double engineActive = combustion + pulsation + starter;
        double engineFriction = e.FrictionTorqueNm.Evaluate(Math.Abs(rpm));

        // Vehicle loads
        var ch = p.Chassis;
        double aeroN = -0.5 * p.Environment.AirDensityKgM3 * ch.DragAreaM2 * _v * Math.Abs(_v);
        double externalN = gravityN + aeroN;
        double brakeN = Math.Max(brakePedal * ch.MaxBrakeForceN, _hillHold.ForceN)
            + (input.Handbrake ? ch.HandbrakeForceN : 0);
        double vehicleFrictionN = ch.RollingResistanceCoeff * mass * gravity * Math.Cos(slope) + brakeN;

        double clutchCapacity = engagement * p.Clutch.MaxTorqueNm;
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

        _x += _v * dt;
        _steps++;

        double idleUsage = ic.MaxTorqueNm > 0 ? idleTorque / ic.MaxTorqueNm : 1;
        State = BuildState(grade, throttle, idleTorque, idleUsage, combustion + pulsation, engineFriction,
            shudderIntensity, engagement, clutchTorque, firing, (_v - previousSpeed) / dt);
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

    private void AdvanceFiringPhase(ShudderParams s, double dt)
    {
        const double cycle = 2 * Math.PI;
        _firingPhase += 2 * _we * dt;
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
        };
    }
}
