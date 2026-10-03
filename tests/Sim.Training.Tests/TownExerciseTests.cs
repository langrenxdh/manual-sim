using Sim.Core;

namespace Sim.Training.Tests;

/// <summary>
/// S11-S13: scripted drivers on the town map (M9e). Steering is pure pursuit along a hand-made path;
/// the pedals follow a target speed with the same clutch habits as the hill-road scripts.
/// </summary>
public class TownExerciseTests
{
    private static readonly TownMap Town =
        TownMap.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", "town.json")));
    private static readonly VehicleParams Golf = Configs.Golf();

    /// <summary>Runs one attempt the way the app does: steering on, from the exercise's town start.</summary>
    private static ExerciseSession Run(string id, Func<double, SimState, DriverInput> driver, double maxSeconds = 150) =>
        RunWithState(id, driver, maxSeconds).Session;

    private static (ExerciseSession Session, SimState Last) RunWithState(string id, Func<double, SimState, DriverInput> driver,
        double maxSeconds = 150)
    {
        var config = Configs.Exercises();
        var exercise = config.Find(id);
        var start = Town.Start(exercise.TownStart!);
        var vehicle = ExerciseSession.ApplyTo(exercise, Golf);
        var sim = new Simulator(vehicle, Road.Flat(), steering: true, startX: start.X, startY: start.Y,
            headingRad: start.HeadingDeg * Math.PI / 180);
        var session = new ExerciseSession(config, exercise, vehicle, town: Town);
        int steps = (int)(maxSeconds / Simulator.StepS);
        for (int i = 0; i < steps && session.Phase == AttemptPhase.Running; i++)
        {
            var input = driver(i * Simulator.StepS, sim.State);
            sim.Step(input);
            session.Observe(sim.State, input, Simulator.StepS);
        }
        return (session, sim.State);
    }

    /// <summary>Why an attempt ended where it did, for assertion messages.</summary>
    private static string Describe((ExerciseSession Session, SimState Last) r) =>
        $"{r.Session.Phase} {r.Session.FailReason} at ({r.Last.WorldX:F1}, {r.Last.WorldY:F1}) heading " +
        $"{r.Last.HeadingRad * 180 / Math.PI:F0} deg, {r.Last.SpeedKmh:F1} km/h, gear {r.Last.EngagedGear}, " +
        $"t {r.Session.Metrics.ElapsedS:F1} s, off road {r.Session.Metrics.OffRoadS:F1} s, via {r.Session.Live.ViaDone}, " +
        $"stalls {r.Session.Metrics.Stalls}";

    /// <summary>
    /// Pedals and lever for a target speed in a wanted gear: clutch in to change gear or stop, then out
    /// to the bite quickly and slowly from there with a little throttle; brakes when too fast.
    /// Pressing the brake with the clutch in is also how it stops at a point (<see cref="StopAt"/>).
    /// </summary>
    private sealed class Pedals
    {
        private double _clutch = 1, _last, _clutchDownFor;
        private Gear _lever = Gear.Neutral;

        public DriverInput Step(double t, in SimState s, Gear want, double targetKmh, double wheelDeg, double? brake = null)
        {
            double dt = t - _last;
            _last = t;
            double v = Math.Abs(s.SpeedKmh);
            bool wrongWay = v > 0.5 && (want == Gear.Reverse ? s.SpeedMps > 0 : s.SpeedMps < 0);
            if (brake != null || targetKmh <= 0 || wrongWay || (_lever != want && _clutch < 1))
            {
                // Stop (or prepare a gear change): clutch down, brake as asked.
                _clutch = 1;
                _clutchDownFor = 0;
                double b = brake ?? (targetKmh <= 0 || wrongWay ? 0.35 : 0);
                return new DriverInput(1, 0, b, _lever, SteeringWheelDeg: wheelDeg);
            }
            if (_lever != want)
            {
                _clutchDownFor += dt;
                if (_clutchDownFor >= 0.3) _lever = want;
                return new DriverInput(1, 0, v < 0.5 ? 0.2 : 0, _lever == want ? want : Gear.Neutral, SteeringWheelDeg: wheelDeg);
            }
            _clutch = _clutch > 0.6 ? Math.Max(0.6, _clutch - dt / 0.15) : Math.Max(0, _clutch - dt * 0.6 / 1.5);
            double throttle = Math.Clamp(0.08 + 0.03 * (targetKmh - v), 0, 0.3);
            return new DriverInput(_clutch, throttle, v > targetKmh + 4 ? 0.15 : 0, _lever, SteeringWheelDeg: wheelDeg);
        }

        /// <summary>Brake pedal that stops the car in <paramref name="remainingM"/> (constant deceleration).</summary>
        public static double StopAt(in SimState s, double remainingM)
        {
            if (remainingM <= 0.05) return 1;
            double need = s.SpeedMps * s.SpeedMps / (2 * remainingM);
            double full = Golf.Chassis.MaxBrakeForceN / Golf.Chassis.MassKg;
            return Math.Clamp(need / full, 0, 1);
        }
    }

    /// <summary>Pure pursuit: the wheel angle that arcs the car onto the path point a lookahead ahead.</summary>
    private sealed class Pursuit((double X, double Y)[] path, double lookaheadM)
    {
        private int _index;

        public double WheelDeg(in SimState s)
        {
            // Advance along the path while the next point is within the lookahead.
            while (_index < path.Length - 1 && Dist(s, path[_index]) < lookaheadM) _index++;
            var (tx, ty) = path[_index];
            double alpha = Math.Atan2(ty - s.WorldY, tx - s.WorldX) - s.HeadingRad;
            alpha = Math.IEEERemainder(alpha, 2 * Math.PI);
            double ld = Math.Max(Dist(s, path[_index]), 1);
            double road = Math.Atan(2 * Golf.Steering.WheelbaseM * Math.Sin(alpha) / ld);
            return road * 180 / Math.PI * Golf.Steering.SteeringRatio;
        }

        public bool AtEnd(in SimState s) => _index == path.Length - 1;

        private static double Dist(in SimState s, (double X, double Y) p) => Math.Sqrt(Sq(p.X - s.WorldX) + Sq(p.Y - s.WorldY));
    }

    private static double Sq(double v) => v * v;

    // ---- S11 roundabout ------------------------------------------------------------------------------

    /// <summary>Up the left lane, clockwise round the ring (radius 12) to the east exit, then along it.</summary>
    private static (double, double)[] RoundaboutPath(bool clockwise)
    {
        var rb = Town.Roundabout;
        double cx = rb.Center[0], cy = rb.Center[1], r = (rb.IslandRadiusM + rb.OuterRadiusM) / 2;
        var path = new List<(double, double)>();
        for (double y = 150; y <= 196; y += 2) path.Add((258.25, y));
        // From the south (-90 degrees) to the east (0): clockwise is the long way, through west and north.
        double from = -90, to = clockwise ? -360 : 0, step = clockwise ? -5 : 5;
        for (double a = from; clockwise ? a >= to : a <= to; a += step)
            path.Add((cx + r * Math.Cos(a * Math.PI / 180), cy + r * Math.Sin(a * Math.PI / 180)));
        for (double x = 280; x <= 360; x += 2) path.Add((x, 217.75));
        return path.ToArray();
    }

    private static Func<double, SimState, DriverInput> RoundaboutDriver(bool clockwise)
    {
        var pedals = new Pedals();
        var pursuit = new Pursuit(RoundaboutPath(clockwise), 6);
        bool second = false;
        return (t, s) =>
        {
            double wheel = pursuit.WheelDeg(s);
            second |= s.SpeedKmh > 14;
            return pedals.Step(t, s, t < 0.5 ? Gear.Neutral : second ? Gear.Second : Gear.First, t < 0.5 ? 0 : 20, wheel);
        };
    }

    [Fact]
    public void S11_Roundabout_ClockwiseToTheEastExit_FinishesOnTheRoadWithAGoodScore()
    {
        var r = RunWithState("roundabout", RoundaboutDriver(clockwise: true));
        var s = r.Session;

        Assert.True(s.Phase == AttemptPhase.Completed, Describe(r));
        Assert.True(s.Metrics.OffRoadS < 0.5, $"off road {s.Metrics.OffRoadS:F1} s");
        Assert.True(s.Result!.Score >= 75, $"score {s.Result.Score:F1}");
    }

    [Fact]
    public void S11b_Roundabout_AntiClockwiseShortCut_NeverFinishes()
    {
        var s = Run("roundabout", RoundaboutDriver(clockwise: false));

        Assert.Equal(AttemptPhase.Failed, s.Phase);
        Assert.Equal(0, s.Live.ViaDone);
        Assert.StartsWith("not finished", s.FailReason);
    }

    // ---- S12 parking bay ------------------------------------------------------------------------------

    private static Func<double, SimState, DriverInput> BayDriver(int bay, double stopShortM = 0)
    {
        var b = Town.Bays().ElementAt(bay);
        var car = Configs.Exercises().TownCar!;
        double bx = (b.MinX + b.MaxX) / 2;
        // Stop with the car centred front to back in the bay.
        double stopY = (b.MinY + b.MaxY) / 2 + (car.RearOfReferenceM - car.FrontOfReferenceM) / 2 - stopShortM;
        var path = new List<(double, double)>();
        for (double y = 250; y <= 300; y += 2) path.Add((424.25, y));
        for (double f = 0; f <= 1; f += 0.1) path.Add((424.25 + (bx - 424.25) * f, 300 + 12 * f));
        for (double y = 314; y <= stopY + 10; y += 1) path.Add((bx, y));
        var pedals = new Pedals();
        var pursuit = new Pursuit(path.ToArray(), 5);
        return (t, s) =>
        {
            double wheel = pursuit.WheelDeg(s);
            double remaining = stopY - s.WorldY;
            if (t < 0.5) return pedals.Step(t, s, Gear.Neutral, 0, wheel);
            if (remaining < 6)
            {
                // Coasted to a stop short of the point: creep on again.
                if (Math.Abs(s.SpeedKmh) < 1.5 && remaining > 0.4) return pedals.Step(t, s, Gear.First, 3, wheel);
                return pedals.Step(t, s, Gear.First, 4, wheel, Pedals.StopAt(s, remaining));
            }
            return pedals.Step(t, s, Gear.First, remaining < 15 ? 6 : 12, wheel);
        };
    }

    [Fact]
    public void S12_CarPark_DriveIntoTheBayAndStop_Finishes()
    {
        var r = RunWithState("carParkBay", BayDriver(bay: 1));
        var s = r.Session;

        Assert.True(s.Phase == AttemptPhase.Completed, Describe(r));
        Assert.True(s.Metrics.OffRoadS < 0.5, $"off road {s.Metrics.OffRoadS:F1} s");
        Assert.True(s.Result!.Score >= 75, $"score {s.Result.Score:F1}");
    }

    [Fact]
    public void S12b_CarPark_TheWrongBay_DoesNotCount()
    {
        var s = Run("carParkBay", BayDriver(bay: 3));

        Assert.Equal(AttemptPhase.Failed, s.Phase);
        Assert.StartsWith("not finished", s.FailReason);
    }

    // ---- S13 three-point turn ------------------------------------------------------------------------

    /// <summary>
    /// Wheel at full lock before moving, forward to the right until a front corner would leave the road
    /// within <paramref name="kerbMarginM"/>, stop, reverse on left lock until a rear corner would, and so
    /// on until pointing south; then straight on in the left lane. A negative margin drives over the kerbs.
    /// </summary>
    private static Func<double, SimState, DriverInput> ThreePointTurn(double kerbMarginM)
    {
        var car = Configs.Exercises().TownCar!;
        double fullLock = Golf.Steering.MaxRoadWheelAngleDeg * Golf.Steering.SteeringRatio;
        var pedals = new Pedals();
        bool forward = true, stopping = false, final = false;
        return (t, s) =>
        {
            double err = Math.IEEERemainder(s.HeadingRad * 180 / Math.PI - 270, 360);
            double lockDeg = forward ? -fullLock : fullLock;
            if (t < 0.5) return pedals.Step(t, s, Gear.Neutral, 0, lockDeg);
            if (final)
                return pedals.Step(t, s, Gear.First, 6, Math.Clamp(-err * 10 + (262 - s.WorldX) * 40, -fullLock, fullLock));
            if (!stopping)
            {
                var corners = car.Corners(s.WorldX, s.WorldY, s.HeadingRad);
                var leading = forward ? corners[..2] : corners[2..];
                double dir = forward ? kerbMarginM : -kerbMarginM;
                double px = Math.Cos(s.HeadingRad) * dir, py = Math.Sin(s.HeadingRad) * dir;
                stopping = leading.Any(c => !Town.OnRoad(c.X + px, c.Y + py)) || Math.Abs(err) < 10;
            }
            if (stopping)
            {
                if (Math.Abs(s.SpeedKmh) > 0.2) // firm, then easing off just before the stop
                    return pedals.Step(t, s, forward ? Gear.First : Gear.Reverse, 0, lockDeg, Math.Abs(s.SpeedKmh) > 3 ? 0.4 : 0.1);
                stopping = false;
                if (Math.Abs(err) < 20) final = true;
                else forward = !forward;
                return pedals.Step(t, s, forward ? Gear.First : Gear.Reverse, 0, final ? 0 : forward ? -fullLock : fullLock, 0.3);
            }
            return pedals.Step(t, s, forward ? Gear.First : Gear.Reverse, 4, lockDeg);
        };
    }

    [Fact]
    public void S13_ThreePointTurn_StayingOffTheKerbs_Finishes()
    {
        var r = RunWithState("threePointTurn", ThreePointTurn(kerbMarginM: 0.8));
        var s = r.Session;

        Assert.True(s.Phase == AttemptPhase.Completed, Describe(r));
        Assert.True(s.Metrics.OffRoadS < 0.5, $"off road {s.Metrics.OffRoadS:F1} s");
        Assert.True(s.Result!.Score >= 75, $"score {s.Result.Score:F1}");
    }

    [Fact]
    public void S13b_ThreePointTurn_OverTheKerbs_ScoresLower()
    {
        var careful = Run("threePointTurn", ThreePointTurn(kerbMarginM: 0.8)).Result!;
        var sloppy = Run("threePointTurn", ThreePointTurn(kerbMarginM: -1.0)).Result!;

        Assert.True(sloppy.Metrics.OffRoadS > careful.Metrics.OffRoadS + 1, $"off road {sloppy.Metrics.OffRoadS:F1} s");
        Assert.True(sloppy.Score < careful.Score - 15, $"sloppy {sloppy.Score:F1} vs careful {careful.Score:F1}");
    }

    // ---- configuration --------------------------------------------------------------------------------

    [Fact]
    public void EveryTownExercise_MatchesTheTownMap()
    {
        var config = Configs.Exercises();
        foreach (var e in config.Exercises.Where(e => e.TownStart != null))
        {
            var session = new ExerciseSession(config, e, Golf, town: Town); // throws on a bad start or bay
            Assert.NotNull(session.FinishZone);
        }
    }

    [Fact]
    public void TownExercise_WithoutTheMap_IsRejected()
    {
        var config = Configs.Exercises();
        Assert.Throws<ArgumentException>(() => new ExerciseSession(config, config.Find("roundabout"), Golf));
    }
}
