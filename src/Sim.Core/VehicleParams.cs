using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sim.Core;

/// <summary>
/// Every tunable of the physics model. Loaded from <c>config/*.json</c>; the core never reads files,
/// callers pass the JSON text to <see cref="FromJson"/>.
/// Units are in the property names; engine speeds are in rpm for readability.
/// </summary>
public sealed record VehicleParams
{
    public required string Name { get; init; }
    public required EngineParams Engine { get; init; }
    public required IdleControlParams IdleControl { get; init; }
    public required ClutchParams Clutch { get; init; }
    public required GearboxParams Gearbox { get; init; }
    public required ChassisParams Chassis { get; init; }
    public required EnvironmentParams Environment { get; init; }
    public required HillHoldParams HillHold { get; init; }
    public required ShudderParams Shudder { get; init; }
    public required ClutchThermalParams ClutchThermal { get; init; }
    public required EngineThermalParams EngineThermal { get; init; }
    public required AirConParams AirCon { get; init; }
    public required SteeringParams Steering { get; init; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static VehicleParams FromJson(string json)
    {
        var p = JsonSerializer.Deserialize<VehicleParams>(json, JsonOptions)
            ?? throw new JsonException("Empty vehicle parameter file.");
        p.Validate();
        return p;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Throws <see cref="ArgumentException"/> if a value is physically meaningless.</summary>
    public void Validate()
    {
        Positive(Engine.InertiaKgM2, "engine.inertiaKgM2");
        Positive(Engine.StallRpm, "engine.stallRpm");
        Require(Engine.IdleRpm > Engine.StallRpm, "engine.idleRpm must be above engine.stallRpm");
        Require(Engine.FuelCutRpm > Engine.IdleRpm, "engine.fuelCutRpm must be above engine.idleRpm");
        Positive(Engine.BoostTimeConstantS, "engine.boostTimeConstantS");
        Positive(Engine.StarterFreeRpm, "engine.starterFreeRpm");
        Require(IdleControl.MaxTorqueNm >= 0, "idleControl.maxTorqueNm must be >= 0");
        Positive(Clutch.MaxTorqueNm, "clutch.maxTorqueNm");
        Require(Clutch.BiteZoneWidth > 0 && Clutch.BitePoint - Clutch.BiteZoneWidth >= 0 && Clutch.BitePoint <= 1,
            "clutch bite zone must lie within pedal travel [0, 1]");
        Positive(Clutch.EngagementExponent, "clutch.engagementExponent");
        Require(Gearbox.ForwardRatios.Length == 6, "gearbox.forwardRatios must list 6 gears");
        Require(Gearbox.ForwardRatios.All(r => r > 0) && Gearbox.ReverseRatio > 0 && Gearbox.FinalDriveRatio > 0,
            "gear ratios must be positive");
        Require(Gearbox.Efficiency is > 0 and <= 1, "gearbox.efficiency must be in (0, 1]");
        Positive(Gearbox.InputShaftInertiaKgM2, "gearbox.inputShaftInertiaKgM2");
        Positive(Chassis.MassKg, "chassis.massKg");
        Positive(Chassis.TyreCircumferenceM, "chassis.tyreCircumferenceM");
        Require(Shudder.FadeOutRpm > Engine.StallRpm, "shudder.fadeOutRpm must be above engine.stallRpm");
        Positive(HillHold.HoldTimeS, "hillHold.holdTimeS");
        Positive(HillHold.ReleaseRateNPerS, "hillHold.releaseRateNPerS");
        var ct = ClutchThermal;
        Positive(ct.HeatCapacityJPerK, "clutchThermal.heatCapacityJPerK");
        Require(ct.CoolingWPerK >= 0, "clutchThermal.coolingWPerK must be >= 0");
        Require(ct.FadeEndC > ct.FadeStartC, "clutchThermal.fadeEndC must be above fadeStartC");
        Require(ct.MinFrictionFactor is > 0 and <= 1, "clutchThermal.minFrictionFactor must be in (0, 1]");
        var et = EngineThermal;
        Positive(et.HeatCapacityJPerK, "engineThermal.heatCapacityJPerK");
        Require(et.HeatPerMechanicalW >= 0 && et.CoolingWPerK >= 0 && et.ThermostatWPerK >= 0,
            "engineThermal heat and cooling rates must be >= 0");
        Require(et.WarmC > et.ColdReferenceC, "engineThermal.warmC must be above coldReferenceC");
        Require(et.ColdFrictionExtra >= 0 && et.ColdIdleExtraRpm >= 0, "engineThermal cold extras must be >= 0");
        Require(AirCon.LoadNm >= 0 && AirCon.IdleBumpRpm >= 0, "airCon values must be >= 0");
        var st = Steering;
        Positive(st.WheelbaseM, "steering.wheelbaseM");
        Require(st.CgToFrontAxleM > 0 && st.CgToFrontAxleM < st.WheelbaseM, "steering.cgToFrontAxleM must lie within the wheelbase");
        Positive(st.SteeringRatio, "steering.steeringRatio");
        Require(st.MaxRoadWheelAngleDeg is > 0 and < 60, "steering.maxRoadWheelAngleDeg must be in (0, 60)");
        Positive(st.TyreGripMu, "steering.tyreGripMu");
        Require(st.MechanicalTrailM >= 0 && st.PneumaticTrailM >= 0, "steering trails must be >= 0");
        Positive(st.PneumaticTrailFadeRatio, "steering.pneumaticTrailFadeRatio");
        Require(st.PowerAssistFactor is > 0 and <= 1, "steering.powerAssistFactor must be in (0, 1]");
    }

    private static void Positive(double value, string name) => Require(value > 0, $"{name} must be > 0");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}

public sealed record EngineParams
{
    /// <summary>Crank + flywheel (both DMF halves) + clutch pressure plate.</summary>
    public required double InertiaKgM2 { get; init; }
    public required double IdleRpm { get; init; }
    /// <summary>Below this speed combustion cannot sustain itself and produces no torque.</summary>
    public required double StallRpm { get; init; }
    /// <summary>Start of the red zone on the tachometer. Display only; no physics uses it.</summary>
    public required double RedlineRpm { get; init; }
    /// <summary>ECU rev limiter: no combustion at or above this speed.</summary>
    public required double FuelCutRpm { get; init; }
    /// <summary>Gross (indicated) combustion torque at full throttle with no boost, by rpm.</summary>
    public required Curve NaTorqueNm { get; init; }
    /// <summary>Extra gross combustion torque at full throttle and full boost (B = 1), by rpm.</summary>
    public required Curve BoostTorqueNm { get; init; }
    /// <summary>Boost the turbo can reach at full throttle (0..1), by rpm. Target = throttle * this.</summary>
    public required Curve BoostTarget { get; init; }
    public required double BoostTimeConstantS { get; init; }
    /// <summary>Internal friction + pumping torque magnitude, by rpm. Always opposes rotation.</summary>
    public required Curve FrictionTorqueNm { get; init; }
    /// <summary>Starter motor torque at the crank when the engine is stationary.</summary>
    public required double StarterStallTorqueNm { get; init; }
    /// <summary>Crank speed at which starter torque falls to zero (linear DC motor curve).</summary>
    public required double StarterFreeRpm { get; init; }
}

/// <summary>ECU idle speed PI controller, acting in the torque domain.</summary>
public sealed record IdleControlParams
{
    public required double ProportionalGainNmPerRpm { get; init; }
    public required double IntegralGainNmPerRpmS { get; init; }
    /// <summary>Upper limit of the gross combustion torque the controller may request.</summary>
    public required double MaxTorqueNm { get; init; }
}

public sealed record ClutchParams
{
    public required double MaxTorqueNm { get; init; }
    /// <summary>Pedal depression (0 = up, 1 = floor) at which the clutch starts to transmit torque.</summary>
    public required double BitePoint { get; init; }
    /// <summary>Pedal travel over which engagement goes from 0 to 1 (ends at BitePoint - BiteZoneWidth).</summary>
    public required double BiteZoneWidth { get; init; }
    /// <summary>Shape of engagement across the bite zone: c = x^exponent. 1 = linear.</summary>
    public required double EngagementExponent { get; init; }
    /// <summary>A gear can only be engaged while clutch engagement is at or below this value.</summary>
    public required double ShiftMaxEngagement { get; init; }
}

public sealed record GearboxParams
{
    /// <summary>Ratios of gears 1 to 6.</summary>
    public required double[] ForwardRatios { get; init; }
    public required double ReverseRatio { get; init; }
    public required double FinalDriveRatio { get; init; }
    public required double Efficiency { get; init; }
    /// <summary>Clutch disc + input shaft, rotating with the clutch's driven side.</summary>
    public required double InputShaftInertiaKgM2 { get; init; }
    /// <summary>Oil churning / bearing drag on the input shaft.</summary>
    public required double InputShaftDragNm { get; init; }
}

public sealed record ChassisParams
{
    /// <summary>Vehicle + driver.</summary>
    public required double MassKg { get; init; }
    public required double TyreCircumferenceM { get; init; }
    public required double RollingResistanceCoeff { get; init; }
    /// <summary>Drag coefficient times frontal area (Cd * A).</summary>
    public required double DragAreaM2 { get; init; }
    /// <summary>Total service brake force at full pedal.</summary>
    public required double MaxBrakeForceN { get; init; }
    public required double HandbrakeForceN { get; init; }
}

public sealed record EnvironmentParams
{
    public required double GravityMps2 { get; init; }
    public required double AirDensityKgM3 { get; init; }
    /// <summary>Air temperature: what the clutch and a cold engine start from and cool towards.</summary>
    public required double AmbientTempC { get; init; }
}

/// <summary>
/// Clutch heating (M7). Slip power heats a lumped mass that cools to ambient; above
/// <see cref="FadeStartC"/> the friction coefficient falls linearly to <see cref="MinFrictionFactor"/>
/// at <see cref="FadeEndC"/> (fade: less capacity, more slip).
/// </summary>
public sealed record ClutchThermalParams
{
    /// <summary>Effective heat capacity of the friction surfaces and pressure plate.</summary>
    public required double HeatCapacityJPerK { get; init; }
    public required double CoolingWPerK { get; init; }
    public required double FadeStartC { get; init; }
    public required double FadeEndC { get; init; }
    public required double MinFrictionFactor { get; init; }
    /// <summary>Above this the driver would smell the clutch: a warning only, no physics.</summary>
    public required double SmellAboveC { get; init; }
}

/// <summary>
/// Engine temperature (M7). Waste heat (a multiple of the mechanical combustion power) warms a lumped
/// mass that cools to ambient; a thermostat adds strong cooling above <see cref="WarmC"/>. Below it the
/// "cold factor" rises linearly to 1 at <see cref="ColdReferenceC"/>, adding internal friction and
/// raising the ECU idle target.
/// </summary>
public sealed record EngineThermalParams
{
    public required double HeatCapacityJPerK { get; init; }
    /// <summary>Waste heat per watt of mechanical combustion power (roughly (1 - efficiency) / efficiency).</summary>
    public required double HeatPerMechanicalW { get; init; }
    public required double CoolingWPerK { get; init; }
    /// <summary>Operating temperature; the thermostat holds the engine here.</summary>
    public required double WarmC { get; init; }
    public required double ThermostatWPerK { get; init; }
    /// <summary>At or below this the cold effects are complete.</summary>
    public required double ColdReferenceC { get; init; }
    /// <summary>Extra internal friction at full cold, as a fraction of the warm friction curve.</summary>
    public required double ColdFrictionExtra { get; init; }
    /// <summary>ECU idle target raise at full cold.</summary>
    public required double ColdIdleExtraRpm { get; init; }
}

/// <summary>
/// Steering and lateral grip (M9b): a kinematic single-track ("bicycle") model with a grip limit.
/// The path curvature follows the road-wheel angle until lateral acceleration reaches
/// <see cref="TyreGripMu"/> x g, beyond which the car understeers. The self-aligning torque felt at the
/// steering wheel is the front lateral force times the trail, through the ratio and the power assist;
/// the pneumatic trail fades once the front tyres slide, so the wheel goes light.
/// </summary>
public sealed record SteeringParams
{
    public required double WheelbaseM { get; init; }
    /// <summary>Centre of gravity to the front axle; sets the front axle's share of the weight.</summary>
    public required double CgToFrontAxleM { get; init; }
    /// <summary>Steering-wheel angle per road-wheel angle.</summary>
    public required double SteeringRatio { get; init; }
    public required double MaxRoadWheelAngleDeg { get; init; }
    /// <summary>Lateral grip: the largest lateral acceleration is this times g.</summary>
    public required double TyreGripMu { get; init; }
    /// <summary>Caster trail: the geometric part of the aligning lever.</summary>
    public required double MechanicalTrailM { get; init; }
    /// <summary>Tyre (pneumatic) trail below the grip limit.</summary>
    public required double PneumaticTrailM { get; init; }
    /// <summary>The pneumatic trail is gone once the demanded lateral acceleration exceeds the limit by this fraction.</summary>
    public required double PneumaticTrailFadeRatio { get; init; }
    /// <summary>Share of the aligning torque the driver feels through the power steering.</summary>
    public required double PowerAssistFactor { get; init; }
}

/// <summary>Air-conditioning compressor (M7): a load torque on the crank and an ECU idle raise while on.</summary>
public sealed record AirConParams
{
    public required double LoadNm { get; init; }
    public required double IdleBumpRpm { get; init; }
}

/// <summary>Golf hill-start assist, modelled as retained brake pressure.</summary>
public sealed record HillHoldParams
{
    public required bool Enabled { get; init; }
    /// <summary>Minimum road grade (rise/run, e.g. 0.03 = 3 %) for the system to arm.</summary>
    public required double MinGrade { get; init; }
    /// <summary>Brake pedal position that counts as "brake pressed".</summary>
    public required double BrakePedalThreshold { get; init; }
    public required double StandstillSpeedMps { get; init; }
    public required double HoldTimeS { get; init; }
    /// <summary>Retained brake force while armed / holding.</summary>
    public required double HoldForceN { get; init; }
    public required double ReleaseRateNPerS { get; init; }
    /// <summary>Release early once uphill drive force reaches this fraction of the downhill gravity force.</summary>
    public required double DriveAwayForceRatio { get; init; }
}

/// <summary>Firing-frequency torque pulsation (shudder / lugging).</summary>
public sealed record ShudderParams
{
    /// <summary>Pulsation amplitude as a fraction of combustion torque at the lowest running speed.</summary>
    public required double MaxAmplitudeRatio { get; init; }
    /// <summary>Above this engine speed there is no pulsation; it grows linearly down to stall speed.</summary>
    public required double FadeOutRpm { get; init; }
    /// <summary>Random cycle-to-cycle amplitude variation (0 = perfectly even firing).</summary>
    public required double Irregularity { get; init; }
    public required ulong Seed { get; init; }
}
