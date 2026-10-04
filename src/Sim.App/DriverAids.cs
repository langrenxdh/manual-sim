using System.Numerics;
using Raylib_cs;
using Sim.Core;
using Sim.Training;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>
/// What a driver uses to judge the car's size and position: the own car's body seen from the seat
/// (bonnet, dashboard, A-pillars), the interior and wing mirrors, and an optional top-down view. Sizes
/// come from <c>camera.json</c> and the car outline (<c>townCar</c> in <c>exercises.json</c>). Each mirror
/// and the overhead view draw the world again from their own camera. Presentation only.
/// </summary>
public sealed class DriverAids : IDisposable
{
    private const float BodyClearanceM = 0.25f, BodyHeightM = 0.8f, CabinHeightM = 0.45f, CabinWidthShare = 0.82f, CabinLengthShare = 0.55f;
    private const float FrameWidthPx = 3, NoseTaperM = 0.1f, RoofDepthM = 0.4f;

    private static readonly Color BodyColour = new(200, 202, 206, 255);
    private static readonly Color BonnetColour = new(150, 152, 158, 255);
    private static readonly Color DashColour = new(28, 28, 32, 255);
    private static readonly Color PillarColour = new(40, 40, 45, 255);
    private static readonly Color HeadlinerColour = new(120, 120, 125, 255);
    private static readonly Color GlassColour = new(40, 50, 60, 255);
    private static readonly Color OutlineColour = new(255, 210, 40, 255);
    private static readonly Color FrameColour = new(15, 15, 18, 255);

    private readonly Dictionary<string, RenderTexture2D> _mirrors = [];
    private RenderTexture2D _overhead;
    private bool _hasOverhead;

    public bool MirrorsOn { get; set; } = true;
    public bool OverheadOn { get; set; }

    /// <summary>The car body around the driver's eye; call inside the driver's 3D pass.</summary>
    public static void DrawCockpit(Camera3D eye, CameraParams c, TownCarDef car)
    {
        var k = c.Cockpit;
        if (!k.Enabled) return;
        var (f, r, u) = CarBasis(eye, c);
        Vector3 P(double ahead, double lateral, double height) =>
            eye.Position + f * (float)ahead + r * (float)(lateral - c.DriverLateralOffsetM) + u * (float)(height - c.EyeHeightM);

        double half = car.WidthM / 2, bonnet = half - k.BonnetInsetM, nose = car.FrontOfReferenceM;
        // Bonnet: from the windscreen's base, sloping down and narrowing slightly to the nose.
        Quad(P(k.CowlAheadM, -bonnet, k.CowlHeightM), P(k.CowlAheadM, bonnet, k.CowlHeightM),
            P(nose, bonnet - NoseTaperM, k.NoseHeightM), P(nose, -bonnet + NoseTaperM, k.NoseHeightM), BonnetColour);
        // Dashboard top and its face down to the floor.
        Quad(P(k.DashAheadM, -half, k.DashHeightM), P(k.DashAheadM, half, k.DashHeightM),
            P(k.CowlAheadM, half, k.CowlHeightM), P(k.CowlAheadM, -half, k.CowlHeightM), DashColour);
        Quad(P(k.DashAheadM, -half, k.DashHeightM), P(k.DashAheadM, half, k.DashHeightM),
            P(k.DashAheadM, half, 0), P(k.DashAheadM, -half, 0), DashColour);
        // A-pillars from the windscreen's corners up to the roof, and the roof's front edge.
        foreach (int side in new[] { -1, 1 })
        {
            double baseLat = side * (half - k.PillarWidthM), topLat = side * (half - 2 * k.PillarWidthM);
            Quad(P(k.CowlAheadM, baseLat, k.CowlHeightM), P(k.CowlAheadM, baseLat + side * k.PillarWidthM, k.CowlHeightM),
                P(k.PillarTopAheadM, topLat + side * k.PillarWidthM, k.PillarTopHeightM), P(k.PillarTopAheadM, topLat, k.PillarTopHeightM),
                PillarColour);
        }
        Quad(P(k.PillarTopAheadM, -half, k.PillarTopHeightM), P(k.PillarTopAheadM, half, k.PillarTopHeightM),
            P(k.PillarTopAheadM - RoofDepthM, half, k.PillarTopHeightM), P(k.PillarTopAheadM - RoofDepthM, -half, k.PillarTopHeightM),
            HeadlinerColour);
    }

    /// <summary>Draws each mirror over the road view, from its spot on the car, mirrored left to right.</summary>
    public void DrawMirrors(IWorldView world, Camera3D eye, in SimState s, CameraParams c, TownCarDef car, Rectangle road)
    {
        if (!MirrorsOn) return;
        var (f, r, u) = CarBasis(eye, c);
        var pose = world.CarPose(s);
        foreach (var m in c.Mirrors)
        {
            int w = Math.Max(8, (int)(road.Width * m.WidthShare)), h = Math.Max(4, (int)(w / m.Aspect));
            var target = Target(m.Name, w, h);
            var position = eye.Position + f * (float)m.AheadM + r * (float)(m.LateralM - c.DriverLateralOffsetM)
                           + u * (float)(m.HeightM - c.EyeHeightM);
            double yaw = m.YawOutDeg * Math.PI / 180 * Math.Sign(m.LateralM);
            var look = -f * (float)Math.Cos(yaw) + r * (float)Math.Sin(yaw);
            var camera = new Camera3D
            {
                Position = position,
                Target = position + look,
                Up = u,
                FovY = (float)m.FovDeg,
                Projection = CameraProjection.Perspective,
            };
            world.Render(target, camera, s, lookingBack: true, m.ShowsOwnCar ? () => DrawOwnCar(pose, car, outline: false) : null);
            var dest = new Rectangle(road.X + (float)(m.ScreenX * road.Width) - w / 2f, road.Y + (float)(m.ScreenY * road.Height) - h / 2f, w, h);
            // Render textures are upside down; a mirror also swaps left and right: flip both ways.
            DrawTexturePro(target.Texture, new Rectangle(0, 0, -w, -h), dest, Vector2.Zero, 0, Color.White);
            DrawRectangleLinesEx(dest, FrameWidthPx, FrameColour);
        }
    }

    /// <summary>The top-down inset, the car pointing up, its outline highlighted.</summary>
    public void DrawOverhead(IWorldView world, in SimState s, CameraParams c, TownCarDef car, Rectangle road)
    {
        if (!OverheadOn) return;
        int size = Math.Max(16, (int)(road.Height * c.Overhead.SizeShare));
        if (_hasOverhead && _overhead.Texture.Width != size)
        {
            UnloadRenderTexture(_overhead);
            _hasOverhead = false;
        }
        if (!_hasOverhead)
        {
            _overhead = LoadRenderTexture(size, size);
            _hasOverhead = true;
        }
        var pose = world.CarPose(s);
        var centre = pose.Ground + pose.Forward * (float)((car.FrontOfReferenceM - car.RearOfReferenceM) / 2);
        var camera = new Camera3D
        {
            Position = centre + Vector3.UnitY * 80,
            Target = centre,
            Up = Vector3.Normalize(pose.Forward with { Y = 0 }),
            FovY = (float)c.Overhead.SpanM,
            Projection = CameraProjection.Orthographic,
        };
        world.Render(_overhead, camera, s, lookingBack: true, () => DrawOwnCar(pose, car, outline: true));
        var dest = new Rectangle(road.X + (float)(c.Overhead.ScreenX * road.Width) - size / 2f,
            road.Y + (float)(c.Overhead.ScreenY * road.Height) - size / 2f, size, size);
        DrawTexturePro(_overhead.Texture, new Rectangle(0, 0, size, -size), dest, Vector2.Zero, 0, Color.White);
        DrawRectangleLinesEx(dest, FrameWidthPx, FrameColour);
    }

    /// <summary>The own car as a body and a cabin on its outline; with an outline on the ground for the overhead view.</summary>
    private static void DrawOwnCar((Vector3 Ground, Vector3 Forward) pose, TownCarDef car, bool outline)
    {
        var f = pose.Forward;
        var r = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitY));
        var u = Vector3.Cross(r, f);
        float length = (float)(car.FrontOfReferenceM + car.RearOfReferenceM), width = (float)car.WidthM;
        var mid = pose.Ground + f * (float)((car.FrontOfReferenceM - car.RearOfReferenceM) / 2);
        OrientedBox(mid + u * (BodyClearanceM + BodyHeightM / 2), f, r, u, length, width, BodyHeightM, BodyColour);
        OrientedBox(mid + u * (BodyClearanceM + BodyHeightM + CabinHeightM / 2) - f * (length * 0.08f), f, r, u,
            length * CabinLengthShare, width * CabinWidthShare, CabinHeightM, GlassColour);
        if (!outline) return;
        float hl = length / 2, hw = width / 2, lift = BodyClearanceM + BodyHeightM + CabinHeightM + 0.05f;
        var a = mid + f * hl - r * hw + u * lift;
        var b = mid + f * hl + r * hw + u * lift;
        var c = mid - f * hl + r * hw + u * lift;
        var d = mid - f * hl - r * hw + u * lift;
        const float band = 0.12f;
        Quad(a, b, b - f * band, a - f * band, OutlineColour);                   // nose
        Quad(d, c, c + f * band, d + f * band, OutlineColour);                   // tail
        Quad(a, d, d + r * band, a + r * band, OutlineColour);                   // left
        Quad(b, c, c - r * band, b - r * band, OutlineColour);                   // right
    }

    private static void OrientedBox(Vector3 centre, Vector3 f, Vector3 r, Vector3 u, float length, float width, float height, Color colour)
    {
        Vector3 P(float x, float y, float z) => centre + f * (x * length / 2) + r * (z * width / 2) + u * (y * height / 2);
        Color side = StaticMesh.Shade(colour, 0.85f), end = StaticMesh.Shade(colour, 0.7f);
        Quad(P(-1, 1, -1), P(1, 1, -1), P(1, 1, 1), P(-1, 1, 1), colour);
        Quad(P(-1, -1, -1), P(1, -1, -1), P(1, 1, -1), P(-1, 1, -1), side);
        Quad(P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), side);
        Quad(P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), end);
        Quad(P(1, -1, -1), P(1, -1, 1), P(1, 1, 1), P(1, 1, -1), end);
    }

    private static void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color colour)
    {
        DrawTriangle3D(a, b, c, colour);
        DrawTriangle3D(a, c, d, colour);
    }

    /// <summary>
    /// The car's forward, right and up directions at the driver's eye: the camera's, turned back up by the
    /// eyes' look-down angle (body pitch, road grade and shake stay: they move the car too).
    /// </summary>
    private static (Vector3 F, Vector3 R, Vector3 U) CarBasis(Camera3D camera, CameraParams c)
    {
        var f = Vector3.Normalize(camera.Target - camera.Position);
        var r = Vector3.Normalize(Vector3.Cross(f, camera.Up));
        var u = Vector3.Cross(r, f);
        float look = (float)(c.LookDownDeg * Math.PI / 180);
        return (f * MathF.Cos(look) + u * MathF.Sin(look), r, u * MathF.Cos(look) - f * MathF.Sin(look));
    }

    private RenderTexture2D Target(string name, int width, int height)
    {
        if (_mirrors.TryGetValue(name, out var t))
        {
            if (t.Texture.Width == width && t.Texture.Height == height) return t;
            UnloadRenderTexture(t);
        }
        return _mirrors[name] = LoadRenderTexture(width, height);
    }

    public void Dispose()
    {
        foreach (var t in _mirrors.Values) UnloadRenderTexture(t);
        _mirrors.Clear();
        if (_hasOverhead) UnloadRenderTexture(_overhead);
        _hasOverhead = false;
    }
}
