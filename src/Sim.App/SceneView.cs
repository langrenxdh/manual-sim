using System.Numerics;
using Raylib_cs;
using Sim.Core;
using Sim.Training;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>
/// The 3D driver's view (docs/design.md 反馈通道 / design.en.md Feedback channels): the road with its
/// hill and stop line, evenly spaced roadside references for optical flow, and a camera that pitches
/// with the road, with body pitch from longitudinal acceleration, and shakes with judder.
/// World axes: X along the road (horizontal run), Y up, Z to the right. Presentation only.
/// </summary>
public sealed class SceneView : IWorldView, IDisposable
{
    // Road layout (metres). Driving on the left: the ego lane is centred on Z = 0, the dashed
    // centre line is on its right, then the opposite lane. Australian markings: 3 m dash, 9 m gap.
    private const float LaneWidth = 3.5f;
    private const float Shoulder = 0.6f;
    private const float LineWidth = 0.12f;
    private const float DashLength = 3, DashPeriod = 12;
    private const float StopLineDepth = 0.4f;
    private const float GroundHalfWidth = 400;
    private const float SegmentM = 2;
    private const float BehindM = 30;
    /// <summary>How far behind the car the mirrors see.</summary>
    private const float MirrorBehindM = 200;
    private const float MarkingLift = 0.01f;
    private const double ProfileStepM = 0.5;
    private const double ProfileLengthM = 20000;

    // Roadside references.
    private const float PoleSpacing = 40, PoleHeight = 7, PoleOffset = 3.5f;
    private const float RailPostSpacing = 4, RailPostHeight = 0.8f, RailPostOffset = 1.2f, RailDrawM = 250;
    private const float DelineatorSpacing = 25, DelineatorHeight = 1.0f, DelineatorOffset = 1.0f;
    private const float BlockCellM = 60;

    private static readonly Color Sky = new(150, 185, 215, 255);
    private static readonly Color Grass = new(85, 120, 70, 255);
    private static readonly Color Asphalt = new(70, 70, 75, 255);
    private static readonly Color Marking = new(235, 235, 235, 255);
    private static readonly Color Pole = new(150, 150, 155, 255);
    private static readonly Color RailPost = new(200, 200, 200, 255);
    private static readonly Color[] BlockColours =
    [
        new(120, 110, 100, 255), new(140, 130, 115, 255), new(95, 105, 115, 255), new(60, 95, 60, 255),
    ];

    private Scene _scene;
    private Curve _grade;
    private double[] _runX = [], _height = [];
    private RenderTexture2D _target;
    private bool _hasTarget;
    private double _bodyPitchRad, _bodyPitchRate;
    private uint _rng = 0xC0FFEE;
    private double? _leadRearM;
    private bool _leadBraking;
    private float _behindM = BehindM;

    // Lead car (M6 traffic queue): a hatchback-sized box with a cabin, in the ego lane.
    private const float LeadLength = 4.3f, LeadWidth = 1.8f, BodyHeight = 0.75f, BodyClearance = 0.3f;
    private const float CabinLength = 2.2f, CabinHeight = 0.6f, LampSize = 0.18f;
    private static readonly Color LeadBody = new(40, 70, 130, 255);
    private static readonly Color LeadGlass = new(30, 35, 45, 255);
    private static readonly Color LampOff = new(110, 30, 30, 255);
    private static readonly Color LampOn = new(255, 40, 30, 255);

    public CameraParams Camera { get; set; }

    /// <summary>Vertical field of view; set from first-time setup, otherwise the camera default.</summary>
    public double? VerticalFovDeg { get; set; }

    public SceneView(Scene scene, CameraParams camera)
    {
        Camera = camera;
        _scene = scene;
        _grade = scene.GradeCurve();
        BuildProfile();
    }

    public Scene Scene
    {
        get => _scene;
        set
        {
            _scene = value;
            _grade = value.GradeCurve();
            BuildProfile();
        }
    }

    /// <summary>Advances the camera's body-pitch model; call once per rendered frame.</summary>
    public void Update(in SimState s, double dtS, VehicleParams p)
    {
        var c = Camera;
        double omega = 2 * Math.PI * c.BodyPitchFrequencyHz;
        double target = c.BodyPitchDegPerG * Math.PI / 180 * s.AccelerationMps2 / p.Environment.GravityMps2;
        // Semi-implicit Euler, sub-stepped so a slow frame cannot make the spring unstable.
        int steps = Math.Max(1, (int)Math.Ceiling(dtS * omega / 0.2));
        double h = dtS / steps;
        for (int i = 0; i < steps; i++)
        {
            double accel = omega * omega * (target - _bodyPitchRad) - 2 * c.BodyPitchDampingRatio * omega * _bodyPitchRate;
            _bodyPitchRate += accel * h;
            _bodyPitchRad += _bodyPitchRate * h;
        }
    }

    /// <param name="leadRearM">Rear bumper of an exercise's lead car (M6 queue), or null for none.</param>
    /// <param name="extra3D">Drawn inside the driver's 3D view after the world (the cockpit), given the camera.</param>
    /// <returns>The driver's camera used for this frame.</returns>
    public Camera3D Draw(in SimState s, Rectangle area, double? leadRearM = null, bool leadBraking = false,
        Action<Camera3D>? extra3D = null)
    {
        _leadRearM = leadRearM;
        _leadBraking = leadBraking;
        EnsureTarget((int)area.Width, (int)area.Height);
        var camera = BuildCamera(s);
        Render(_target, camera, s, lookingBack: false, extra3D == null ? null : () => extra3D(camera));

        // Render textures are stored upside down: flip on the way out.
        var source = new Rectangle(0, 0, _target.Texture.Width, -_target.Texture.Height);
        DrawTexturePro(_target.Texture, source, area, Vector2.Zero, 0, Color.White);
        return camera;
    }

    /// <inheritdoc/>
    public void Render(RenderTexture2D target, Camera3D camera, in SimState s, bool lookingBack, Action? extra3D)
    {
        _behindM = lookingBack ? MirrorBehindM : BehindM;
        BeginTextureMode(target);
        ClearBackground(Sky);
        BeginMode3D(camera);
        Rlgl.DisableBackfaceCulling();
        DrawRoad(s.PositionM);
        DrawRoadside(s.PositionM);
        DrawLeadCar();
        extra3D?.Invoke();
        Rlgl.DrawRenderBatchActive(); // the batch draws later; flush while culling is still off
        Rlgl.EnableBackfaceCulling();
        EndMode3D();
        EndTextureMode();
    }

    /// <inheritdoc/>
    public (Vector3 Ground, Vector3 Forward) CarPose(in SimState s)
    {
        var g = PointAt(s.PositionM);
        double slope = Math.Atan(_grade.Evaluate(s.PositionM));
        return (new Vector3(g.X, g.Y, 0), new Vector3((float)Math.Cos(slope), (float)Math.Sin(slope), 0));
    }

    private Camera3D BuildCamera(in SimState s)
    {
        var c = Camera;
        double jitter = s.ShudderIntensity;
        double pitch = Math.Atan(_grade.Evaluate(s.PositionM)) + _bodyPitchRad - c.LookDownDeg * Math.PI / 180
                       + jitter * c.ShakePitchDeg * Math.PI / 180 * NextSigned();
        var ground = PointAt(s.PositionM);
        var eye = new Vector3(ground.X, ground.Y + (float)(c.EyeHeightM + jitter * c.ShakeHeightM * NextSigned()),
            (float)c.DriverLateralOffsetM);
        var forward = new Vector3((float)Math.Cos(pitch), (float)Math.Sin(pitch), 0);
        return new Camera3D
        {
            Position = eye,
            Target = eye + forward * 10,
            Up = Vector3.UnitY,
            FovY = (float)(VerticalFovDeg ?? c.DefaultVerticalFovDeg),
            Projection = CameraProjection.Perspective,
        };
    }

    private void DrawRoad(double position)
    {
        float roadLeft = -LaneWidth / 2 - Shoulder, roadRight = LaneWidth * 1.5f + Shoulder;
        float leftLine = -LaneWidth / 2, centreLine = LaneWidth / 2, rightLine = LaneWidth * 1.5f;
        double start = Math.Floor((position - _behindM) / SegmentM) * SegmentM;
        double end = position + Camera.DrawDistanceM;

        for (double a = start; a < end; a += SegmentM)
        {
            double b = a + SegmentM;
            Strip(a, b, -GroundHalfWidth, roadLeft, 0, Grass);
            Strip(a, b, roadRight, GroundHalfWidth, 0, Grass);
            Strip(a, b, roadLeft, roadRight, 0, Asphalt);
            Strip(a, b, leftLine - LineWidth / 2, leftLine + LineWidth / 2, MarkingLift, Marking);
            Strip(a, b, rightLine - LineWidth / 2, rightLine + LineWidth / 2, MarkingLift, Marking);
        }

        for (double d = Math.Floor(start / DashPeriod) * DashPeriod; d < end; d += DashPeriod)
            Strip(d, d + DashLength, centreLine - LineWidth / 2, centreLine + LineWidth / 2, MarkingLift, Marking);

        double stop = _scene.StopLineM;
        Strip(stop - StopLineDepth, stop, leftLine, centreLine, MarkingLift, Marking);
    }

    private void DrawRoadside(double position)
    {
        double end = position + Camera.DrawDistanceM;
        float leftLine = -LaneWidth / 2, rightLine = LaneWidth * 1.5f;

        for (double s = Math.Ceiling((position - _behindM) / PoleSpacing) * PoleSpacing; s < end; s += PoleSpacing)
        {
            var g = PointAt(s);
            var basePos = new Vector3(g.X, g.Y, leftLine - PoleOffset);
            DrawCylinder(basePos, 0.08f, 0.12f, PoleHeight, 6, Pole);
            DrawCube(basePos + new Vector3(0, PoleHeight, 1.2f), 0.15f, 0.15f, 2.4f, Pole);
        }

        for (double s = Math.Ceiling((position - _behindM) / RailPostSpacing) * RailPostSpacing;
             s < position + RailDrawM; s += RailPostSpacing)
        {
            var g = PointAt(s);
            DrawCube(new Vector3(g.X, g.Y + RailPostHeight / 2, rightLine + RailPostOffset), 0.12f, RailPostHeight, 0.12f, RailPost);
        }

        for (double s = Math.Ceiling((position - _behindM) / DelineatorSpacing) * DelineatorSpacing; s < end; s += DelineatorSpacing)
        {
            var g = PointAt(s);
            DrawCube(new Vector3(g.X, g.Y + DelineatorHeight / 2, leftLine - DelineatorOffset), 0.1f, DelineatorHeight, 0.1f, Marking);
        }

        // Distant blocks (buildings, tree lines) for horizon reference, fixed per 60 m cell.
        for (long cell = (long)Math.Floor((position - _behindM) / BlockCellM); cell * BlockCellM < end; cell++)
        {
            uint h = Hash((uint)cell);
            float s = (float)(cell * BlockCellM + h % 40);
            float side = (h & 1) == 0 ? -1 : 1;
            float lateral = side * (25 + (h >> 3) % 90);
            float w = 6 + (h >> 9) % 14, d = 6 + (h >> 13) % 14, ht = 4 + (h >> 17) % 20;
            var g = PointAt(s);
            DrawCube(new Vector3(g.X, g.Y + ht / 2, lateral), d, ht, w, BlockColours[(h >> 22) % BlockColours.Length]);
        }
    }

    private void DrawLeadCar()
    {
        if (_leadRearM is not double rear) return;
        var p = PointAt(rear + LeadLength / 2);
        float bodyY = p.Y + BodyClearance + BodyHeight / 2;
        DrawCube(new Vector3(p.X, bodyY, 0), LeadLength, BodyHeight, LeadWidth, LeadBody);
        DrawCube(new Vector3(p.X - 0.2f, bodyY + BodyHeight / 2 + CabinHeight / 2, 0), CabinLength, CabinHeight, LeadWidth - 0.15f, LeadGlass);
        var tail = PointAt(rear);
        Color lamp = _leadBraking ? LampOn : LampOff;
        foreach (float z in new[] { -LeadWidth / 2 + 0.2f, LeadWidth / 2 - 0.2f })
            DrawCube(new Vector3(tail.X - 0.01f, bodyY + 0.15f, z), 0.05f, LampSize, LampSize * 2, lamp);
    }

    /// <summary>A flat quad across the road from z0 to z1, between road positions a and b.</summary>
    private void Strip(double a, double b, float z0, float z1, float lift, Color colour)
    {
        var pa = PointAt(a);
        var pb = PointAt(b);
        var v1 = new Vector3(pa.X, pa.Y + lift, z0);
        var v2 = new Vector3(pb.X, pb.Y + lift, z0);
        var v3 = new Vector3(pb.X, pb.Y + lift, z1);
        var v4 = new Vector3(pa.X, pa.Y + lift, z1);
        DrawTriangle3D(v1, v2, v3, colour);
        DrawTriangle3D(v1, v3, v4, colour);
    }

    /// <summary>Road centre-line point (horizontal run, height) at distance s along the road.</summary>
    private Vector2 PointAt(double s)
    {
        if (s <= 0) return new Vector2((float)s, 0);
        double i = s / ProfileStepM;
        int last = _runX.Length - 1;
        if (i >= last) return new Vector2((float)(_runX[last] + (s - last * ProfileStepM)), (float)_height[last]);
        int k = (int)i;
        double t = i - k;
        return new Vector2((float)(_runX[k] + t * (_runX[k + 1] - _runX[k])),
            (float)(_height[k] + t * (_height[k + 1] - _height[k])));
    }

    /// <summary>Integrates the grade curve into a road profile: distance along the road to (run, height).</summary>
    private void BuildProfile()
    {
        int n = (int)(ProfileLengthM / ProfileStepM) + 1;
        _runX = new double[n];
        _height = new double[n];
        for (int i = 1; i < n; i++)
        {
            double slope = Math.Atan(_grade.Evaluate((i - 0.5) * ProfileStepM));
            _runX[i] = _runX[i - 1] + ProfileStepM * Math.Cos(slope);
            _height[i] = _height[i - 1] + ProfileStepM * Math.Sin(slope);
        }
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
