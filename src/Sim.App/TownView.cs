using System.Numerics;
using Raylib_cs;
using Sim.Core;
using Sim.Training;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>
/// The driver's view on the flat town map (M9c): roads, the roundabout, the car park with its bays and
/// buildings off the road, seen from the car's world pose. Map X east / Y north become raylib X / -Z
/// (Y is up). The geometry is built once per map; body pitch and judder shake come from
/// <see cref="SceneView"/>'s camera settings. Presentation only.
/// </summary>
public sealed class TownView : IDisposable
{
    private const float LineWidth = 0.12f, MarkingLift = 0.01f, IslandLift = 0.15f;
    private const float DashLength = 3, DashPeriod = 12;
    private const float GroundHalfSize = 2000;
    private const int RingSegments = 72;
    private const double BlockCellM = 30, BlockClearanceM = 6;
    private const double CentreLineMinWidthM = 6.5;

    private static readonly Color Sky = new(150, 185, 215, 255);
    private static readonly Color Grass = new(85, 120, 70, 255);
    private static readonly Color Asphalt = new(70, 70, 75, 255);
    private static readonly Color Marking = new(235, 235, 235, 255);
    private static readonly Color Kerb = new(190, 190, 185, 255);
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

        var cp = map.CarPark;
        double x0 = cp.Corner[0], y0 = cp.Corner[1], x1 = x0 + cp.Size[0], y1 = y0 + cp.Size[1];
        Quad(At(x0, y0), At(x1, y0), At(x1, y1), At(x0, y1), Asphalt);
        foreach (var (minX, minY, maxX, _) in map.Bays())
        {
            foreach (double bx in new[] { minX, maxX })
                Quad(At(bx - LineWidth / 2, minY, MarkingLift), At(bx + LineWidth / 2, minY, MarkingLift),
                    At(bx + LineWidth / 2, y1, MarkingLift), At(bx - LineWidth / 2, y1, MarkingLift), Marking);
            Quad(At(minX, minY, MarkingLift), At(maxX, minY, MarkingLift),
                At(maxX, minY + LineWidth, MarkingLift), At(minX, minY + LineWidth, MarkingLift), Marking);
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

        var rb = map.Roundabout;
        Ring(rb.Center[0], rb.Center[1], rb.IslandRadiusM, rb.OuterRadiusM, 0.005f, Asphalt);
        Ring(rb.Center[0], rb.Center[1], rb.IslandRadiusM, rb.IslandRadiusM + 0.4, IslandLift / 2 + 0.01f, Kerb);
        Ring(rb.Center[0], rb.Center[1], 0, rb.IslandRadiusM, IslandLift, Grass);

        // Buildings and tree lines on a fixed grid, kept clear of every road, so turning shows motion.
        for (int gx = -10; gx < 30; gx++)
            for (int gy = -10; gy < 20; gy++)
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

    public void Draw(in SimState s, Rectangle area)
    {
        EnsureTarget((int)area.Width, (int)area.Height);
        BeginTextureMode(_target);
        ClearBackground(Sky);
        BeginMode3D(BuildCamera(s));
        Rlgl.DisableBackfaceCulling();
        foreach (var (a, b, c, colour) in _triangles) DrawTriangle3D(a, b, c, colour);
        foreach (var (centre, size, colour) in _blocks) DrawCubeV(centre, size, colour);
        Rlgl.DrawRenderBatchActive();
        Rlgl.EnableBackfaceCulling();
        EndMode3D();
        EndTextureMode();
        var source = new Rectangle(0, 0, _target.Texture.Width, -_target.Texture.Height);
        DrawTexturePro(_target.Texture, source, area, Vector2.Zero, 0, Color.White);
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
