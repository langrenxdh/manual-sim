using Raylib_cs;
using Sim.App;
using Sim.Core;
using Sim.Input;
using static Raylib_cs.Raylib;

// M2 drivable prototype (docs/design.md, 开发里程碑).
// Keys: Tab tuning panel, R restart at the road start, H restart on the hill (also the right paddle),
// P replay of the last 10 s (also triangle), F11 borderless fullscreen, Esc quit.
// --screenshot FILE [--panel] [--hill] [--replay] [--frames N]: save a frame after start-up and exit (checks the visuals without a
// person); --hill also works on its own to start on the hill.
string? screenshotPath = args.Length >= 2 && args[0] == "--screenshot" ? Path.GetFullPath(args[1]) : null;
bool screenshotPanel = args.Contains("--panel");
int screenshotFrame = args.SkipWhile(a => a != "--frames").Skip(1).Select(int.Parse).FirstOrDefault(90);
int frameCount = 0;

var config = ConfigFiles.Locate();
var vehicle = VehicleParams.FromJson(config.Read(ConfigFiles.VehicleFile));
var input = InputConfig.FromJson(config.Read(ConfigFiles.InputFile));
var sound = SoundParams.FromJson(config.Read(ConfigFiles.SoundFile));
var scene = Scene.FromJson(config.Read(ConfigFiles.SceneFile));
var camera = CameraParams.FromJson(config.Read(ConfigFiles.CameraFile));
var ffbParams = FfbParams.FromJson(config.Read(ConfigFiles.FfbFile));
SceneView? sceneView = null;
const float RoadViewShare = 0.72f; // road on the top ~3/4, instruments below (design doc)
EngineSound? engineSound = null;
bool onHill = false;
UiButtons previousButtons = default;

using var physics = new PhysicsLoop(vehicle, input, scene.BuildRoad());
using var ffb = new FfbLoop(physics.ForFfb, ffbParams);
using var telemetry = new TelemetryWriter(physics.ForTelemetry,
    Path.GetFullPath(Path.Combine(config.Directory, "..", "telemetry")));
bool replayOpen = false;

void ToggleReplay()
{
    replayOpen = !replayOpen;
    if (replayOpen) telemetry.RequestSnapshot();
}

void Restart(bool hill)
{
    onHill = hill;
    physics.Reset(scene.BuildRoad(), hill ? scene.HillStartPositionM : 0, engageHandbrake: hill);
}

var panel = new TuningPanel(
[
    new TunableDocument("Vehicle", ConfigFiles.VehicleFile, typeof(VehicleParams),
        config.Read(ConfigFiles.VehicleFile), VehicleParams.FromJson,
        o => physics.SubmitParams(vehicle = (VehicleParams)o)),
    new TunableDocument("Input", ConfigFiles.InputFile, typeof(InputConfig),
        config.Read(ConfigFiles.InputFile), InputConfig.FromJson,
        o => physics.SubmitInputConfig((InputConfig)o)),
    new TunableDocument("Sound", ConfigFiles.SoundFile, typeof(SoundParams),
        config.Read(ConfigFiles.SoundFile), SoundParams.FromJson,
        o => { sound = (SoundParams)o; if (engineSound != null) engineSound.Params = sound; }),
    new TunableDocument("Scene (changing it restarts the car)", ConfigFiles.SceneFile, typeof(Scene),
        config.Read(ConfigFiles.SceneFile), Scene.FromJson,
        o => { scene = (Scene)o; if (sceneView != null) sceneView.Scene = scene; Restart(onHill); }),
    new TunableDocument("Camera", ConfigFiles.CameraFile, typeof(CameraParams),
        config.Read(ConfigFiles.CameraFile), CameraParams.FromJson,
        o => { camera = (CameraParams)o; if (sceneView != null) sceneView.Camera = camera; }),
    new TunableDocument("Force feedback", ConfigFiles.FfbFile, typeof(FfbParams),
        config.Read(ConfigFiles.FfbFile), FfbParams.FromJson,
        o => ffb.Params = (FfbParams)o),
], config.Save) { Visible = screenshotPanel };

SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.VSyncHint | ConfigFlags.Msaa4xHint);
InitWindow(1600, 900, "Manual Sim - " + vehicle.Name);
SetTargetFPS(60);
Ui.Load();
sceneView = new SceneView(scene, camera);
if (args.Contains("--hill")) Restart(hill: true);
engineSound = EngineSound.Start(physics.ForAudio, sound);
if (engineSound == null) Console.Error.WriteLine("No audio device: running without engine sound.");

while (!WindowShouldClose())
{
    if (IsKeyPressed(KeyboardKey.Tab)) panel.Visible = !panel.Visible;
    if (IsKeyPressed(KeyboardKey.R)) Restart(hill: false);
    if (IsKeyPressed(KeyboardKey.H)) Restart(hill: true);
    if (IsKeyPressed(KeyboardKey.P)) ToggleReplay();
    if (IsKeyPressed(KeyboardKey.F11)) ToggleBorderlessWindowed();

    float w = GetScreenWidth(), h = GetScreenHeight();
    float panelWidth = panel.Visible ? Math.Min(TuningPanel.Width, w / 2) : 0;
    var viewArea = new Rectangle(0, 0, w - panelWidth, h);
    var panelArea = new Rectangle(w - panelWidth, 0, panelWidth, h);
    panel.Update(panelArea, TuningPanel.ListArea(panelArea));

    var frame = physics.ForRender.Read();
    var ffbStatus = ffb.Status.Read();
    var telemetryStatus = telemetry.Status.Read();
    panel.ExtraReadouts =
    [
        ffbStatus.Connected
            ? $"ffb {ffbStatus.RateHz:F0} Hz   calls {ffbStatus.Calls}   failed {ffbStatus.Failures}   max {ffbStatus.MaxCallMs:F1} ms   jolts {ffbStatus.Jolts}"
            : $"ffb off: {ffbStatus.Error ?? "starting"}",
        telemetryStatus.Error != null
            ? $"telemetry file off: {telemetryStatus.Error}"
            : $"telemetry {telemetryStatus.Samples} samples   dropped {telemetryStatus.Dropped}   {Path.GetFileName(telemetryStatus.File)}",
    ];
    var buttons = frame.Input.Buttons;
    if (buttons.HillStart && !previousButtons.HillStart) Restart(hill: true);
    if (buttons.Replay && !previousButtons.Replay) ToggleReplay();
    previousButtons = buttons;
    BeginDrawing();
    ClearBackground(Color.Black);
    sceneView.Update(frame.State, GetFrameTime(), vehicle);
    float roadHeight = viewArea.Height * RoadViewShare;
    sceneView.Draw(frame.State, new Rectangle(viewArea.X, viewArea.Y, viewArea.Width, roadHeight));
    if (replayOpen)
        ReplayView.Draw(telemetry.TakeSnapshot(), new Rectangle(viewArea.X, viewArea.Y, viewArea.Width, roadHeight),
            vehicle, Dashboard.TachMaxRpm);
    Dashboard.Draw(frame, vehicle, new Rectangle(viewArea.X, viewArea.Y + roadHeight, viewArea.Width, viewArea.Height - roadHeight));
    if (!panel.Visible)
        Ui.Text($"physics {frame.PhysicsHz:F0} Hz  overruns {frame.Overruns}   Tab: tuning panel", 8, h - 24, 18, Color.Gray);
    panel.Draw(panelArea, frame, vehicle);

    if (screenshotPath != null && args.Contains("--replay") && frameCount == screenshotFrame - 30 && !replayOpen)
        ToggleReplay();
    if (screenshotPath != null && ++frameCount == screenshotFrame)
    {
        Rlgl.DrawRenderBatchActive();
        var image = LoadImageFromScreen();
        ExportImage(image, screenshotPath);
        UnloadImage(image);
        Console.WriteLine($"screenshot: rpm {frame.State.EngineRpm:F0} kmh {frame.State.SpeedKmh:F1} " +
                          $"gear {frame.State.EngagedGear} input {frame.Input}");
        EndDrawing();
        break;
    }
    EndDrawing();
}

sceneView.Dispose();
engineSound?.Dispose();
Ui.Unload();
CloseWindow();
