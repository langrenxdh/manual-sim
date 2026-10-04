using System.Numerics;
using Raylib_cs;
using Sim.Core;
using Sim.Training;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>
/// The driver's view on the flat town map (M9c): roads, roundabouts, car parks with their bays and parked
/// cars, and buildings off the road, seen from the car's world pose. Map X east / Y north become raylib X / -Z
/// (Y is up). The geometry is built once per map; body pitch and judder shake come from
/// <see cref="SceneView"/>'s camera settings. Presentation only.
/// </summary>
public sealed class TownView : IDisposable
{
    private const float LineWidth = 0.12f, MarkingLift = 0.01f, IslandLift = 0.15f;
    private const float DashLength = 3, DashPeriod = 12;
    private const float GroundHalfSize = 2000;
    private const int RingSegments = 72;
    private const double BlockCellM = 30, BlockClearanceM = 6, BlockMarginM = 150;
    private const double TwoLaneRingWidthM = 10;
    private const float ParkedBodyHeight = 0.8f, ParkedCabinHeight = 0.5f;
    private const double CentreLineMinWidthM = 6.5;

    private static readonly Color Sky = new(150, 185, 215, 255);
    private static readonly Color Grass = new(85, 120, 70, 255);
    private static readonly Color Asphalt = new(70, 70, 75, 255);
    private static readonly Color Marking = new(235, 235, 235, 255);
    private static readonly Color Kerb = new(190, 190, 185, 255);
    private static readonly Color Goal = new(245, 200, 40, 255);
    private static readonly Color CarGlass = new(40, 50, 60, 255);
    private static readonly Color[] CarColours =
    [
        new(170, 30, 30, 255), new(220, 220, 225, 255), new(30, 60, 140, 255), new(25, 25, 28, 255), new(150, 150, 155, 255),
    ];
    private static readonly Color[] BlockColours =
    [
        new(120, 110, 100, 255), new(140, 130, 115, 255), new(95, 105, 115, 255), new(60, 95, 60, 255),
    ];

    private readonly List<(Vector3 A, Vector3 B, Vector3 C, Color Colour)> _triangles = [];
    private readonly List<(Vector3 Centre, Vector3 Size, Color Colour)> _blocks = [];
    private RenderTexture2D _target;
    private bool _hasTarget;
    private double _bodyPitchRad, _bodyPitchRate;
    private uint _rng = 0xBEEF;

    public CameraParams Camera { get; set; }
    public double? VerticalFovDeg { get; set; }

    public TownView(TownMap map, CameraParams camera)
    {
        Camera = camera;
        Build(map);
    }

    private static Vector3 At(double x, double y, float lift = 0) => new((float)x, lift, (float)-y);

    private void Build(TownMap map)
    {
        Quad(At(-GroundHalfSize, -GroundHalfSize, -0.02f), At(GroundHalfSize, -GroundHalfSize, -0.02f),
            At(GroundHalfSize, GroundHalfSize, -0.02f), At(-GroundHalfSize, GroundHalfSize, -0.02f), Grass);

        for (int k = 0; k < map.CarParks.Length; k++)
        {
            var cp = map.CarParks[k];
            double x0 = cp.Corner[0], y0 = cp.Corner[1], x1 = x0 + cp.Size[0], y1 = y0 + cp.Size[1];
            Quad(At(x0, y0), At(x1, y0), At(x1, y1), At(x0, y1), Asphalt);
            foreach (var (minX, minY, maxX, _) in map.Bays(k))
            {
                foreach (double bx in new[] { minX, maxX })
                    Quad(At(bx - LineWidth / 2, minY, MarkingLift), At(bx + LineWidth / 2, minY, MarkingLift),
                        At(bx + LineWidth / 2, y1, MarkingLift), At(bx - LineWidth / 2, y1, MarkingLift), Marking);
                Quad(At(minX, minY, MarkingLift), At(maxX, minY, MarkingLift),
                    At(maxX, minY + LineWidth, MarkingLift), At(minX, minY + LineWidth, MarkingLift), Marking);
            }
        }
        // Parked cars: a body and a lower glasshouse, so their size reads at a glance.
        int carIndex = 0;
        foreach (var (minX, minY, maxX, maxY) in map.ParkedCars())
        {
            double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
            float w = (float)(maxX - minX), l = (float)(maxY - minY);
            var colour = CarColours[carIndex++ % CarColours.Length];
            _blocks.Add((At(cx, cy, ParkedBodyHeight / 2 + 0.2f), new Vector3(w, ParkedBodyHeight, l), colour));
            _blocks.Add((At(cx, cy, ParkedBodyHeight + 0.2f + ParkedCabinHeight / 2), new Vector3(w * 0.85f, ParkedCabinHeight, l * 0.5f), CarGlass));
        }

        foreach (var (road, pts) in map.Expanded)
        {
            double half = road.WidthM / 2, along = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                var (ax, ay) = pts[i - 1];
                var (bx, by) = pts[i];
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                if (len == 0) continue;
                // Left-hand normal; widen each piece slightly so arcs show no cracks between pieces.
                double nx = -(by - ay) / len, ny = (bx - ax) / len, ex = (bx - ax) / len * 0.05, ey = (by - ay) / len * 0.05;
                Ribbon(ax - ex, ay - ey, bx + ex, by + ey, nx, ny, -half, half, 0, Asphalt);
                foreach (double edge in new[] { -half + 0.3, half - 0.3 })
                    Ribbon(ax, ay, bx, by, nx, ny, edge - LineWidth / 2, edge + LineWidth / 2, MarkingLift, Marking);
                if (road.WidthM >= CentreLineMinWidthM && along % DashPeriod < DashLength)
                    Ribbon(ax, ay, bx, by, nx, ny, -LineWidth / 2, LineWidth / 2, MarkingLift, Marking);
                along += len;
            }
        }

        foreach (var rb in map.Roundabouts)
        {
            double cx = rb.Center[0], cy = rb.Center[1];
            Ring(cx, cy, rb.IslandRadiusM, rb.OuterRadiusM, 0.005f, Asphalt);
            Ring(cx, cy, rb.IslandRadiusM, rb.IslandRadiusM + 0.4, IslandLift / 2 + 0.01f, Kerb);
            Ring(cx, cy, 0, rb.IslandRadiusM, IslandLift, Grass);
            // A wide ring has two lanes: a dashed line between them.
            if (rb.OuterRadiusM - rb.IslandRadiusM >= TwoLaneRingWidthM)
            {
                double mid = (rb.IslandRadiusM + rb.OuterRadiusM) / 2;
                int dashes = (int)(2 * Math.PI * mid / DashPeriod);
                for (int i = 0; i < dashes; i++)
                {
                    double a = 2 * Math.PI * i / dashes, b = a + DashLength / mid;
                    Quad(At(cx + Math.Cos(a) * (mid - LineWidth / 2), cy + Math.Sin(a) * (mid - LineWidth / 2), MarkingLift),
                        At(cx + Math.Cos(a) * (mid + LineWidth / 2), cy + Math.Sin(a) * (mid + LineWidth / 2), MarkingLift),
                        At(cx + Math.Cos(b) * (mid + LineWidth / 2), cy + Math.Sin(b) * (mid + LineWidth / 2), MarkingLift),
                        At(cx + Math.Cos(b) * (mid - LineWidth / 2), cy + Math.Sin(b) * (mid - LineWidth / 2), MarkingLift), Marking);
                }
            }
        }

        // Buildings and tree lines on a fixed grid over the whole map, kept clear of every road, so turning shows motion.
        var all = map.Expanded.SelectMany(r => r.Points).ToList();
        int gx0 = (int)Math.Floor((all.Min(p => p.X) - BlockMarginM) / BlockCellM), gx1 = (int)Math.Ceiling((all.Max(p => p.X) + BlockMarginM) / BlockCellM);
        int gy0 = (int)Math.Floor((all.Min(p => p.Y) - BlockMarginM) / BlockCellM), gy1 = (int)Math.Ceiling((all.Max(p => p.Y) + BlockMarginM) / BlockCellM);
        for (int gx = gx0; gx < gx1; gx++)
            for (int gy = gy0; gy < gy1; gy++)
            {
                uint h = Hash((uint)(gx * 7919 + gy * 104729));
                if (h % 3 == 0) continue;
                double cx = (gx + 0.5) * BlockCellM + (h >> 4) % 10 - 5, cy = (gy + 0.5) * BlockCellM + (h >> 8) % 10 - 5;
                float w = 6 + (h >> 12) % 12, d = 6 + (h >> 16) % 12, ht = 4 + (h >> 20) % 16;
                double reach = Math.Max(w, d) / 2 + BlockClearanceM;
                if (Near(map, cx, cy, reach)) continue;
                _blocks.Add((At(cx, cy, ht / 2), new Vector3(w, ht, d), BlockColours[(h >> 26) % BlockColours.Length]));
            }
    }

    private static bool Near(TownMap map, double x, double y, double reach)
    {
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            if (map.OnRoad(x + Math.Cos(a) * reach, y + Math.Sin(a) * reach)) return true;
        }
        return map.OnRoad(x, y);
    }

    private void Ribbon(double ax, double ay, double bx, double by, double nx, double ny, double o0, double o1, float lift, Color c) =>
        Quad(At(ax + nx * o0, ay + ny * o0, lift), At(bx + nx * o0, by + ny * o0, lift),
            At(bx + nx * o1, by + ny * o1, lift), At(ax + nx * o1, ay + ny * o1, lift), c);

    private void Ring(double cx, double cy, double r0, double r1, float lift, Color c)
    {
        for (int i = 0; i < RingSegments; i++)
        {
            double a = 2 * Math.PI * i / RingSegments, b = 2 * Math.PI * (i + 1) / RingSegments;
            Quad(At(cx + Math.Cos(a) * r0, cy + Math.Sin(a) * r0, lift), At(cx + Math.Cos(a) * r1, cy + Math.Sin(a) * r1, lift),
                At(cx + Math.Cos(b) * r1, cy + Math.Sin(b) * r1, lift), At(cx + Math.Cos(b) * r0, cy + Math.Sin(b) * r0, lift), c);
        }
    }

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color colour)
    {
        _triangles.Add((a, b, c, colour));
        _triangles.Add((a, c, d, colour));
    }

    /// <summary>Advances the camera's body-pitch model; call once per rendered frame.</summary>
    public void Update(in SimState s, double dtS, VehicleParams p)
    {
        var c = Camera;
        double omega = 2 * Math.PI * c.BodyPitchFrequencyHz;
        double target = c.BodyPitchDegPerG * Math.PI / 180 * s.AccelerationMps2 / p.Environment.GravityMps2;
        int steps = Math.Max(1, (int)Math.Ceiling(dtS * omega / 0.2));
        double h = dtS / steps;
        for (int i = 0; i < steps; i++)
        {
            double accel = omega * omega * (target - _bodyPitchRad) - 2 * c.BodyPitchDampingRatio * omega * _bodyPitchRate;
            _bodyPitchRate += accel * h;
            _bodyPitchRad += _bodyPitchRate * h;
        }
    }

    /// <param name="finishZone">A town exercise's goal [minX, minY, maxX, maxY], outlined on the ground; null for none.</param>
    public void Draw(in SimState s, Rectangle area, double[]? finishZone = null)
    {
        EnsureTarget((int)area.Width, (int)area.Height);
        BeginTextureMode(_target);
        ClearBackground(Sky);
        BeginMode3D(BuildCamera(s));
        Rlgl.DisableBackfaceCulling();
        foreach (var (a, b, c, colour) in _triangles) DrawTriangle3D(a, b, c, colour);
        foreach (var (centre, size, colour) in _blocks) DrawCubeV(centre, size, colour);
        if (finishZone is { } z) DrawZone(z);
        Rlgl.DrawRenderBatchActive();
        Rlgl.EnableBackfaceCulling();
        EndMode3D();
        EndTextureMode();
        var source = new Rectangle(0, 0, _target.Texture.Width, -_target.Texture.Height);
        DrawTexturePro(_target.Texture, source, area, Vector2.Zero, 0, Color.White);
    }

    /// <summary>The goal outline: a band just inside the zone's edge, slightly above the markings.</summary>
    private static void DrawZone(double[] z)
    {
        const float band = 0.25f, lift = MarkingLift * 2;
        double x0 = z[0], y0 = z[1], x1 = z[2], y1 = z[3];
        foreach (var (ax, ay, bx, by) in new[]
                 {
                     (x0, y0, x1, y0 + band), (x0, y1 - band, x1, y1), (x0, y0, x0 + band, y1), (x1 - band, y0, x1, y1),
                 })
        {
            DrawTriangle3D(At(ax, ay, lift), At(bx, ay, lift), At(bx, by, lift), Goal);
            DrawTriangle3D(At(ax, ay, lift), At(bx, by, lift), At(ax, by, lift), Goal);
        }
    }

    private Camera3D BuildCamera(in SimState s)
    {
        var c = Camera;
        double jitter = s.ShudderIntensity;
        double pitch = _bodyPitchRad + jitter * c.ShakePitchDeg * Math.PI / 180 * NextSigned();
        double heading = s.HeadingRad;
        // Right-hand drive: the seat sits to the right of the car's centre line.
        double rx = Math.Sin(heading), ry = -Math.Cos(heading);
        var eye = At(s.WorldX + rx * c.DriverLateralOffsetM, s.WorldY + ry * c.DriverLateralOffsetM,
            (float)(c.EyeHeightM + jitter * c.ShakeHeightM * NextSigned()));
        var forward = new Vector3((float)(Math.Cos(heading) * Math.Cos(pitch)), (float)Math.Sin(pitch),
            (float)(-Math.Sin(heading) * Math.Cos(pitch)));
        return new Camera3D
        {
            Position = eye,
            Target = eye + forward * 10,
            Up = Vector3.UnitY,
            FovY = (float)(VerticalFovDeg ?? c.DefaultVerticalFovDeg),
            Projection = CameraProjection.Perspective,
        };
    }

    private void EnsureTarget(int width, int height)
    {
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);
        if (_hasTarget && _target.Texture.Width == width && _target.Texture.Height == height) return;
        if (_hasTarget) UnloadRenderTexture(_target);
        _target = LoadRenderTexture(width, height);
        _hasTarget = true;
    }

    private static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352d;
        x ^= x >> 15; x *= 0x846ca68b;
        x ^= x >> 16;
        return x;
    }

    private double NextSigned()
    {
        _rng ^= _rng << 13;
        _rng ^= _rng >> 17;
        _rng ^= _rng << 5;
        return 2.0 * _rng / uint.MaxValue - 1;
    }

    public void Dispose()
    {
        if (_hasTarget) UnloadRenderTexture(_target);
        _hasTarget = false;
    }
}
