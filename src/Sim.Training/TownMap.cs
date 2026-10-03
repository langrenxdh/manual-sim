using System.Text.Json;

namespace Sim.Training;

/// <summary>
/// The flat town map for driving with steering (M9c), loaded from <c>config/town.json</c>: roads built
/// from straights and arcs, a roundabout, a car park with bays, and named start poses. Positions are
/// metres on the ground plane, X east and Y north; headings in degrees counter-clockwise from east.
/// The physics only needs a flat road; this map decides where "the road" is (for judging and drawing).
/// </summary>
public sealed record TownMap
{
    public required TownRoad[] Roads { get; init; }
    public required RoundaboutDef Roundabout { get; init; }
    public required CarParkDef CarPark { get; init; }
    public required TownStart[] Starts { get; init; }

    /// <summary>Spacing of the expanded road centre-line points.</summary>
    public const double PointSpacingM = 1.0;

    private IReadOnlyList<(TownRoad Road, IReadOnlyList<(double X, double Y)> Points)>? _expanded;

    public static TownMap FromJson(string json)
    {
        var m = JsonSerializer.Deserialize<TownMap>(json, ExerciseConfig.JsonOptions)
            ?? throw new JsonException("Empty town file.");
        m.Validate();
        return m;
    }

    public void Validate()
    {
        ExerciseConfig.Require(Roads.Length > 0, "town: at least one road");
        foreach (var r in Roads)
        {
            ExerciseConfig.Require(r.WidthM > 0, $"town road \"{r.Name}\": widthM must be > 0");
            ExerciseConfig.Require(r.Start.Length == 2 && r.Pieces.Length > 0, $"town road \"{r.Name}\": start [x, y] and pieces");
            foreach (var p in r.Pieces)
                ExerciseConfig.Require(p.StraightM > 0 ^ (p.TurnDeg != 0 && p.RadiusM > 0),
                    $"town road \"{r.Name}\": each piece is a straight (straightM) or an arc (turnDeg, radiusM)");
        }
        var rb = Roundabout;
        ExerciseConfig.Require(rb.Center.Length == 2 && rb.IslandRadiusM > 0 && rb.OuterRadiusM > rb.IslandRadiusM,
            "town roundabout: center [x, y] and 0 < islandRadiusM < outerRadiusM");
        var cp = CarPark;
        ExerciseConfig.Require(cp.Corner.Length == 2 && cp.Size.Length == 2 && cp.Size.All(s => s > 0), "town car park: corner and size");
        ExerciseConfig.Require(cp.BayCount > 0 && cp.BayWidthM > 0 && cp.BayDepthM > 0 && cp.BayCount * cp.BayWidthM <= cp.Size[0],
            "town car park: bays must fit along its width");
        ExerciseConfig.Require(Starts.Length > 0 && Starts.Select(s => s.Id).Distinct().Count() == Starts.Length, "town: unique start ids");
    }

    public TownStart Start(string id) =>
        Starts.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException($"No town start \"{id}\".");

    /// <summary>Each road's centre line as points every <see cref="PointSpacingM"/>.</summary>
    public IReadOnlyList<(TownRoad Road, IReadOnlyList<(double X, double Y)> Points)> Expanded =>
        _expanded ??= Roads.Select(r => (r, (IReadOnlyList<(double, double)>)Expand(r))).ToList();

    private static List<(double X, double Y)> Expand(TownRoad r)
    {
        double x = r.Start[0], y = r.Start[1], heading = r.StartHeadingDeg * Math.PI / 180;
        var pts = new List<(double, double)> { (x, y) };
        foreach (var p in r.Pieces)
        {
            if (p.StraightM > 0)
            {
                int n = Math.Max(1, (int)Math.Ceiling(p.StraightM / PointSpacingM));
                for (int i = 1; i <= n; i++)
                    pts.Add((x + Math.Cos(heading) * p.StraightM * i / n, y + Math.Sin(heading) * p.StraightM * i / n));
                x += Math.Cos(heading) * p.StraightM;
                y += Math.Sin(heading) * p.StraightM;
            }
            else
            {
                double turn = p.TurnDeg * Math.PI / 180, side = Math.Sign(turn);
                double cx = x - Math.Sin(heading) * p.RadiusM * side, cy = y + Math.Cos(heading) * p.RadiusM * side;
                double a0 = Math.Atan2(y - cy, x - cx);
                int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(turn) * p.RadiusM / PointSpacingM));
                for (int i = 1; i <= n; i++)
                {
                    double a = a0 + turn * i / n;
                    pts.Add((cx + Math.Cos(a) * p.RadiusM, cy + Math.Sin(a) * p.RadiusM));
                }
                heading += turn;
                (x, y) = pts[^1];
            }
        }
        return pts;
    }

    /// <summary>True on a road, on the roundabout's carriageway or in the car park.</summary>
    public bool OnRoad(double x, double y)
    {
        var rb = Roundabout;
        double dr = Math.Sqrt(Sq(x - rb.Center[0]) + Sq(y - rb.Center[1]));
        if (dr <= rb.IslandRadiusM) return false;                 // the island is never road
        if (dr <= rb.OuterRadiusM) return true;
        var cp = CarPark;
        if (x >= cp.Corner[0] && x <= cp.Corner[0] + cp.Size[0] && y >= cp.Corner[1] && y <= cp.Corner[1] + cp.Size[1]) return true;
        foreach (var (road, pts) in Expanded)
        {
            double half = road.WidthM / 2;
            for (int i = 1; i < pts.Count; i++)
                if (DistanceToSegment(x, y, pts[i - 1], pts[i]) <= half) return true;
        }
        return false;
    }

    /// <summary>The bays as rectangles (minX, minY, maxX, maxY) along the far (north) edge of the car park.</summary>
    public IEnumerable<(double MinX, double MinY, double MaxX, double MaxY)> Bays()
    {
        var cp = CarPark;
        double top = cp.Corner[1] + cp.Size[1];
        double start = cp.Corner[0] + (cp.Size[0] - cp.BayCount * cp.BayWidthM) / 2;
        for (int i = 0; i < cp.BayCount; i++)
            yield return (start + i * cp.BayWidthM, top - cp.BayDepthM, start + (i + 1) * cp.BayWidthM, top);
    }

    private static double Sq(double v) => v * v;

    private static double DistanceToSegment(double x, double y, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
        double t = len2 == 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / len2, 0, 1);
        return Math.Sqrt(Sq(x - (a.X + t * dx)) + Sq(y - (a.Y + t * dy)));
    }
}

public sealed record TownRoad
{
    public required string Name { get; init; }
    public required double WidthM { get; init; }
    public required double[] Start { get; init; }
    public required double StartHeadingDeg { get; init; }
    public required RoadPiece[] Pieces { get; init; }
}

/// <summary>A straight (<see cref="StraightM"/>) or an arc (<see cref="TurnDeg"/>, positive = left, and <see cref="RadiusM"/>).</summary>
public sealed record RoadPiece
{
    public double StraightM { get; init; }
    public double TurnDeg { get; init; }
    public double RadiusM { get; init; }
}

public sealed record RoundaboutDef
{
    public required double[] Center { get; init; }
    public required double IslandRadiusM { get; init; }
    public required double OuterRadiusM { get; init; }
}

public sealed record CarParkDef
{
    /// <summary>South-west corner [x, y].</summary>
    public required double[] Corner { get; init; }
    /// <summary>[width along X, depth along Y].</summary>
    public required double[] Size { get; init; }
    public required int BayCount { get; init; }
    public required double BayWidthM { get; init; }
    public required double BayDepthM { get; init; }
}

public sealed record TownStart
{
    public required string Id { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double HeadingDeg { get; init; }
}
