namespace Sim.Training;

/// <summary>
/// Parking sensors (like the Golf's ParkPilot) on the town map: four on each bumper, the outer ones angled
/// outwards. Each looks along its direction for the first point that is not road (a parked car, a kerb, the
/// edge of a car park, a roundabout island) and reports how far away it is. Pure: map, car outline and pose
/// in, distances out.
/// </summary>
public static class ParkingSensors
{
    /// <summary>Nearest obstacle ahead of the front and behind the rear bumper, metres; null when that end is off or clear.</summary>
    public readonly record struct Reading(double? FrontM, double? RearM)
    {
        public double? NearestM => FrontM is { } f ? RearM is { } r ? Math.Min(f, r) : f : RearM;
    }

    /// <param name="heading">Radians counter-clockwise from +X.</param>
    /// <param name="speedMps">Signed road speed; the front sensors work below <see cref="ParkingSensorParams.FrontActiveBelowKmh"/>.</param>
    /// <param name="reverse">Reverse is engaged: front and rear sensors both work.</param>
    public static Reading Measure(TownMap map, TownCarDef car, double x, double y, double heading, double speedMps, bool reverse,
        ParkingSensorParams p)
    {
        bool front = reverse || Math.Abs(speedMps) * 3.6 < p.FrontActiveBelowKmh;
        return new Reading(front ? Bumper(map, car, x, y, heading, +1, p.FrontRangeM, p) : null,
            reverse ? Bumper(map, car, x, y, heading, -1, p.RearRangeM, p) : null);
    }

    /// <summary>The nearest hit of the four sensors on one bumper (end = +1 front, -1 rear), or null if none within range.</summary>
    private static double? Bumper(TownMap map, TownCarDef car, double x, double y, double heading, int end, double range,
        ParkingSensorParams p)
    {
        double fx = Math.Cos(heading), fy = Math.Sin(heading), lx = -fy, ly = fx;
        double along = end > 0 ? car.FrontOfReferenceM : -car.RearOfReferenceM, half = car.WidthM / 2;
        double? nearest = null;
        foreach (double share in new[] { -1, -p.InnerSensorShare, p.InnerSensorShare, 1 })
        {
            double sx = x + fx * along + lx * half * share, sy = y + fy * along + ly * half * share;
            // Inner sensors look straight out of the bumper; the corner ones are turned outwards.
            double turn = Math.Abs(share) == 1 ? Math.Sign(share) * p.CornerAngleDeg * Math.PI / 180 : 0;
            double dx = end * fx * Math.Cos(turn) + lx * Math.Sin(turn), dy = end * fy * Math.Cos(turn) + ly * Math.Sin(turn);
            for (double d = 0; d <= range; d += p.StepM)
            {
                if (map.OnRoad(sx + dx * d, sy + dy * d)) continue;
                if (nearest == null || d < nearest) nearest = d;
                break;
            }
        }
        return nearest;
    }
}

/// <summary>Parking sensor geometry and ranges (part of <c>config/parking.json</c>).</summary>
public sealed record ParkingSensorParams
{
    public required double FrontRangeM { get; init; }
    public required double RearRangeM { get; init; }
    /// <summary>Driving forwards, the front sensors work only below this speed.</summary>
    public required double FrontActiveBelowKmh { get; init; }
    /// <summary>The inner sensors sit at this share of the half-width from the centre line; the outer ones at the corners.</summary>
    public required double InnerSensorShare { get; init; }
    /// <summary>The corner sensors look this far outwards from straight ahead (or behind).</summary>
    public required double CornerAngleDeg { get; init; }
    /// <summary>Search step along each sensor's line.</summary>
    public required double StepM { get; init; }

    public void Validate()
    {
        ExerciseConfig.Require(FrontRangeM > 0 && RearRangeM > 0 && FrontActiveBelowKmh >= 0, "parking sensors: ranges > 0, speed >= 0");
        ExerciseConfig.Require(InnerSensorShare is > 0 and < 1 && CornerAngleDeg is >= 0 and < 90 && StepM is > 0 and <= 0.5,
            "parking sensors: innerSensorShare in (0, 1), cornerAngleDeg in [0, 90), stepM in (0, 0.5]");
    }
}
