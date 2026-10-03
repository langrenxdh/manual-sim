using Raylib_cs;
using Sim.App;
using Sim.Core;
using Sim.Input;
using Sim.Training;
using static Raylib_cs.Raylib;

// Manual transmission practice simulator (docs/design.en.md).
// Keys: Tab tuning panel, E exercises, R restart (the road start, or the current exercise),
// H restart on the hill or the current exercise (also the right paddle), P replay of the last 10 s
// (also triangle), T teaching mode (also O), V car, C air-con, F2 first-time setup, F11 borderless fullscreen,
// Esc leaves an exercise, otherwise quits.
// --screenshot FILE [--panel] [--hill] [--replay] [--teaching] [--setup] [--menu] [--progress] [--exercise ID] [--frames N] [--size WxH]:
// save a frame after start-up and exit (checks the visuals without a person). --hill and
// --exercise ID also work on their own.
string? screenshotPath = args.Length >= 2 && args[0] == "--screenshot" ? Path.GetFullPath(args[1]) : null;
bool screenshotPanel = args.Contains("--panel");
int screenshotFrame = args.SkipWhile(a => a != "--frames").Skip(1).Select(int.Parse).FirstOrDefault(90);
// --size WxH sets the initial window size (default 1600x900), e.g. to fit a smaller display.
int[] windowSize = args.SkipWhile(a => a != "--size").Skip(1).Select(s => s.Split('x').Select(int.Parse).ToArray()).FirstOrDefault([1600, 900]);
int frameCount = 0;

var config = ConfigFiles.Locate();
string carFile = ConfigFiles.VehicleFile; // the car menu (V) changes it
var vehicle = VehicleParams.FromJson(config.Read(carFile));
var input = InputConfig.FromJson(config.Read(ConfigFiles.InputFile));
var sound = SoundParams.FromJson(config.Read(ConfigFiles.SoundFile));
var scene = Scene.FromJson(config.Read(ConfigFiles.SceneFile));
var camera = CameraParams.FromJson(config.Read(ConfigFiles.CameraFile));
var ffbParams = FfbParams.FromJson(config.Read(ConfigFiles.FfbFile));
var exercises = ExerciseConfig.FromJson(config.Read(ConfigFiles.ExercisesFile));
// Driving data lives next to config/; unattended screenshot runs use a throwaway folder so they can
// never mix with (or tempt anyone to delete) the real telemetry and score history.
string dataRoot = screenshotPath != null
    ? Path.Combine(Path.GetTempPath(), "manual-sim-screenshot-data")
    : Path.GetFullPath(Path.Combine(config.Directory, ".."));
var scoreHistory = new ScoreHistory(Path.Combine(dataRoot, "scores"));
var exerciseUi = new ExerciseUi();
ExerciseDef? activeExercise = null; // null = free driving
long recordedAttempt = -1;
// Machine-specific setup; until it has run, the FOV comes from camera.json and the rest from the example.
bool setupDone = config.Exists(ConfigFiles.SetupFile);
var setup = SetupParams.FromJson(config.Read(setupDone ? ConfigFiles.SetupFile : ConfigFiles.SetupExampleFile));
SceneView? sceneView = null;
const float RoadViewShare = 0.72f; // road on the top ~3/4, instruments below (design doc)
EngineSound? engineSound = null;
bool onHill = false;
UiButtons previousButtons = default;

using var physics = new PhysicsLoop(vehicle, input, scene.BuildRoad());
using var ffb = new FfbLoop(physics.ForFfb, ffbParams);
using var telemetry = new TelemetryWriter(physics.ForTelemetry,
    Path.Combine(dataRoot, "telemetry"));
bool replayOpen = false;
bool teaching = false;

// The replay covers the road view, where the teaching overlays live, so asking for teaching mode while
// the replay is open closes the replay and shows the overlays instead of toggling them out of sight.
void ToggleTeaching()
{
    if (replayOpen)
    {
        replayOpen = false;
        teaching = true;
    }
    else teaching = !teaching;
}

bool progressOpen = false;
ScoreResult? replayAttempt = null; // set: the replay shows this finished attempt against its ghost

// After an exercise attempt the replay shows that whole attempt with the ghost; otherwise the last 10 s.
void ToggleReplay(ScoreResult? finishedAttempt)
{
    replayOpen = !replayOpen;
    replayAttempt = replayOpen && activeExercise != null ? finishedAttempt : null;
    if (replayOpen && replayAttempt == null) telemetry.RequestSnapshot();
}

void Restart(bool hill)
{
    activeExercise = null;
    onHill = hill;
    physics.Reset(scene.BuildRoad(), hill ? scene.HillStartPositionM : 0, engageHandbrake: hill, scene.StartEngineTempC);
}

void StartExercise(ExerciseDef e)
{
    activeExercise = e;
    bool hill = e.Start == StartPoint.HillStart;
    onHill = hill;
    physics.StartExercise(scene, scene.PositionOf(e.Start), engageHandbrake: hill, exercises, e, scene.StartEngineTempC);
}

// R / H / right paddle retry the current exercise; in free driving they are the two restarts.
void RestartOrRetry(bool hill)
{
    if (activeExercise != null) StartExercise(activeExercise);
    else Restart(hill);
}

// The tuning panel's Vehicle document edits whichever car is selected.
TunableDocument VehicleDocument(string file) =>
    new("Vehicle", file, typeof(VehicleParams), config.Read(file), VehicleParams.FromJson,
        o => physics.SubmitParams(vehicle = (VehicleParams)o));

var carMenu = new CarMenu(config);
TuningPanel? panelRef = null;

void SelectCar(string file)
{
    carFile = file;
    vehicle = VehicleParams.FromJson(config.Read(file));
    physics.SubmitParams(vehicle);
    panelRef?.Replace(VehicleDocument(file));
    if (activeExercise != null) StartExercise(activeExercise);
    else Restart(onHill);
}

var panel = new TuningPanel(
[
    VehicleDocument(carFile),
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
    new TunableDocument("Exercises (apply to the next attempt)", ConfigFiles.ExercisesFile, typeof(ExerciseConfig),
        config.Read(ConfigFiles.ExercisesFile), ExerciseConfig.FromJson,
        o => exercises = (ExerciseConfig)o),
], config.Save) { Visible = screenshotPanel };
panelRef = panel;

SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.VSyncHint | ConfigFlags.Msaa4xHint);
InitWindow(windowSize[0], windowSize[1], "Manual Sim - " + vehicle.Name);
if (!IsWindowReady())
{
    // E.g. a disconnected remote-desktop session has no display to open a window on.
    Console.Error.WriteLine("Could not open the window (no display available?). Exiting.");
    return 1;
}
SetTargetFPS(60);
Ui.Load();
sceneView = new SceneView(scene, camera);
Restart(hill: false); // applies the scene's start temperature from the first drive
if (args.Contains("--hill")) Restart(hill: true);
if (args.Contains("--teaching")) teaching = true;
if (args.SkipWhile(a => a != "--exercise").Skip(1).FirstOrDefault() is { } exerciseId) StartExercise(exercises.Find(exerciseId));
if (args.Contains("--menu")) exerciseUi.MenuOpen = true;
if (args.Contains("--progress")) progressOpen = true;
engineSound = EngineSound.Start(physics.ForAudio, sound);
if (engineSound == null) Console.Error.WriteLine("No audio device: running without engine sound.");
else engineSound.SpeakerLowCutHz = setup.SpeakerLowCutHz;

var setupScreen = new SetupScreen(engineSound, s =>
{
    setup = s;
    setupDone = true;
    config.Save(ConfigFiles.SetupFile, JsonLayout.Write(System.Text.Json.JsonSerializer.SerializeToNode(s, VehicleParams.JsonOptions)!));
    if (engineSound != null) engineSound.SpeakerLowCutHz = s.SpeakerLowCutHz;
});
// Open setup on first run, but not in unattended screenshot runs unless asked for.
if (screenshotPath == null ? !setupDone : args.Contains("--setup")) setupScreen.Open(setup);
SetExitKey(KeyboardKey.Null); // Esc cancels setup when it is open, otherwise quits (below)

while (!WindowShouldClose())
{
    var frame = physics.ForRender.Read();
    var buttons = frame.Input.Buttons;
    if (setupScreen.Active)
    {
        if (IsKeyPressed(KeyboardKey.Escape)) setupScreen.Cancel();
        setupScreen.Update(buttons, GetFrameTime());
    }
    else if (exerciseUi.MenuOpen)
    {
        if (IsKeyPressed(KeyboardKey.Escape) || IsKeyPressed(KeyboardKey.E)) exerciseUi.MenuOpen = false;
        else switch (exerciseUi.UpdateMenu(exercises, buttons, out var chosen))
        {
            case ExerciseUi.MenuChoice.Exercise: StartExercise(chosen!); break;
            case ExerciseUi.MenuChoice.Progress: progressOpen = true; break;
            case ExerciseUi.MenuChoice.FreeDriving: Restart(hill: false); break;
        }
    }
    else if (carMenu.Open)
    {
        if (IsKeyPressed(KeyboardKey.Escape) || IsKeyPressed(KeyboardKey.V)) carMenu.Open = false;
        else if (carMenu.Update(buttons) is { } chosenCar) SelectCar(chosenCar);
    }
    else if (progressOpen)
    {
        exerciseUi.UpdateMenu(exercises, buttons, out _); // keeps D-pad edge tracking current
        if (IsKeyPressed(KeyboardKey.Escape) || IsKeyPressed(KeyboardKey.E)) progressOpen = false;
    }
    else
    {
        exerciseUi.UpdateMenu(exercises, buttons, out _); // keeps D-pad edge tracking current
        if (IsKeyPressed(KeyboardKey.Escape))
        {
            if (activeExercise == null) break;
            Restart(hill: false);
        }
        if (IsKeyPressed(KeyboardKey.E)) exerciseUi.MenuOpen = true;
        if (IsKeyPressed(KeyboardKey.Tab)) panel.Visible = !panel.Visible;
        if (IsKeyPressed(KeyboardKey.R)) RestartOrRetry(hill: false);
        if (IsKeyPressed(KeyboardKey.H)) RestartOrRetry(hill: true);
        if (IsKeyPressed(KeyboardKey.P)) ToggleReplay(frame.Exercise.Result);
        if (IsKeyPressed(KeyboardKey.T)) ToggleTeaching();
        if (IsKeyPressed(KeyboardKey.V)) carMenu.Open = true;
        if (IsKeyPressed(KeyboardKey.C)) physics.AirConOn = !physics.AirConOn;
        if (IsKeyPressed(KeyboardKey.F2)) setupScreen.Open(setup);
        if (buttons.HillStart && !previousButtons.HillStart) RestartOrRetry(hill: true);
        if (buttons.Replay && !previousButtons.Replay) ToggleReplay(frame.Exercise.Result);
        if (buttons.TeachingMode && !previousButtons.TeachingMode) ToggleTeaching();
    }

    // Record each finished attempt once.
    if (frame.Exercise.Result is { } result && frame.Exercise.AttemptId != recordedAttempt)
    {
        recordedAttempt = frame.Exercise.AttemptId;
        scoreHistory.Record(result);
    }
    previousButtons = buttons;
    if (IsKeyPressed(KeyboardKey.F11)) ToggleBorderlessWindowed();

    float w = GetScreenWidth(), h = GetScreenHeight();
    float panelWidth = panel.Visible ? Math.Min(TuningPanel.Width, w / 2) : 0;
    var viewArea = new Rectangle(0, 0, w - panelWidth, h);
    var panelArea = new Rectangle(w - panelWidth, 0, panelWidth, h);
    if (!setupScreen.Active) panel.Update(panelArea, TuningPanel.ListArea(panelArea));
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
    BeginDrawing();
    ClearBackground(Color.Black);
    sceneView.Update(frame.State, GetFrameTime(), vehicle);
    float roadHeight = viewArea.Height * RoadViewShare;
    sceneView.VerticalFovDeg = setupDone ? setup.VerticalFovDeg(roadHeight, GetMonitorWidth(GetCurrentMonitor())) : null;
    sceneView.Draw(frame.State, new Rectangle(viewArea.X, viewArea.Y, viewArea.Width, roadHeight),
        frame.Exercise.Live.LeadPositionM, frame.Exercise.Live.LeadBraking);
    var roadArea = new Rectangle(viewArea.X, viewArea.Y, viewArea.Width, roadHeight);
    if (teaching && !replayOpen) TeachingOverlay.Draw(frame, vehicle, roadArea);
    if (progressOpen)
        ProgressView.Draw(exercises, scoreHistory, roadArea);
    else if (replayOpen && replayAttempt != null)
        AttemptReplayView.Draw(replayAttempt, scoreHistory.Ghost(replayAttempt.ExerciseId), exercises.Coaching,
            roadArea, vehicle, Dashboard.TachMaxRpm);
    else if (replayOpen)
        ReplayView.Draw(telemetry.TakeSnapshot(), roadArea, vehicle, Dashboard.TachMaxRpm);
    else if (activeExercise != null)
    {
        // With teaching mode on, stay between its side boxes so neither covers the other.
        var centre = teaching ? TeachingOverlay.Centre(roadArea) : roadArea;
        ExerciseUi.DrawBanner(frame.Exercise, centre);
        ExerciseUi.DrawResult(frame.Exercise, scoreHistory, centre);
    }
    exerciseUi.DrawMenu(exercises, scoreHistory, teaching ? TeachingOverlay.Centre(roadArea) : roadArea);
    carMenu.Draw(carFile, roadArea);
    Dashboard.Draw(frame, vehicle, scene.Dashboard, new Rectangle(viewArea.X, viewArea.Y + roadHeight, viewArea.Width, viewArea.Height - roadHeight));
    if (!panel.Visible)
        Ui.Text($"physics {frame.PhysicsHz:F0} Hz  overruns {frame.Overruns}   Tab: tuning panel", 8, h - 24, 18, Color.Gray);
    panel.Draw(panelArea, frame, vehicle);
    setupScreen.Draw(new Rectangle(0, 0, w, h));

    if (screenshotPath != null && args.Contains("--replay") && frameCount == screenshotFrame - 30 && !replayOpen)
        ToggleReplay(frame.Exercise.Result);
    if (screenshotPath != null && ++frameCount == screenshotFrame)
    {
        Rlgl.DrawRenderBatchActive();
        var image = LoadImageFromScreen();
        ExportImage(image, screenshotPath);
        UnloadImage(image);
        Console.WriteLine($"screenshot: window {w}x{h} render {GetRenderWidth()}x{GetRenderHeight()} panel {panel.Visible} " +
                          $"rpm {frame.State.EngineRpm:F0} kmh {frame.State.SpeedKmh:F1} " +
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
return 0;
