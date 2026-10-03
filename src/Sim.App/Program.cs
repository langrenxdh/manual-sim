using Raylib_cs;
using Sim.App;
using Sim.Core;
using Sim.Input;
using static Raylib_cs.Raylib;

// M2 drivable prototype (docs/design.md, 开发里程碑).
// Keys: Tab tuning panel, R reset car, F11 borderless fullscreen, Esc quit.
// --screenshot FILE [--panel]: save a frame after start-up and exit (checks the visuals without a person).
string? screenshotPath = args.Length >= 2 && args[0] == "--screenshot" ? Path.GetFullPath(args[1]) : null;
bool screenshotPanel = args.Contains("--panel");
const int ScreenshotFrame = 90;
int frameCount = 0;

var config = ConfigFiles.Locate();
var vehicle = VehicleParams.FromJson(config.Read(ConfigFiles.VehicleFile));
var input = InputConfig.FromJson(config.Read(ConfigFiles.InputFile));
double grade = 0;

using var physics = new PhysicsLoop(vehicle, input);

var panel = new TuningPanel(
[
    new TunableDocument("Vehicle", ConfigFiles.VehicleFile, typeof(VehicleParams),
        config.Read(ConfigFiles.VehicleFile), VehicleParams.FromJson,
        o => physics.SubmitParams(vehicle = (VehicleParams)o)),
    new TunableDocument("Input", ConfigFiles.InputFile, typeof(InputConfig),
        config.Read(ConfigFiles.InputFile), InputConfig.FromJson,
        o => physics.SubmitInputConfig((InputConfig)o)),
    new TunableDocument("Scenario (changing it restarts the car)", null, typeof(Scenario),
        """{ "gradePercent": 0 }""", Scenario.FromJson,
        o => physics.Reset(grade = ((Scenario)o).GradePercent / 100)),
], config.Save) { Visible = screenshotPanel };

SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.VSyncHint | ConfigFlags.Msaa4xHint);
InitWindow(1600, 900, "Manual Sim - " + vehicle.Name);
SetTargetFPS(60);
Ui.Load();

while (!WindowShouldClose())
{
    if (IsKeyPressed(KeyboardKey.Tab)) panel.Visible = !panel.Visible;
    if (IsKeyPressed(KeyboardKey.R)) physics.Reset(grade);
    if (IsKeyPressed(KeyboardKey.F11)) ToggleBorderlessWindowed();

    float w = GetScreenWidth(), h = GetScreenHeight();
    float panelWidth = panel.Visible ? Math.Min(TuningPanel.Width, w / 2) : 0;
    var viewArea = new Rectangle(0, 0, w - panelWidth, h);
    var panelArea = new Rectangle(w - panelWidth, 0, panelWidth, h);
    panel.Update(panelArea, TuningPanel.ListArea(panelArea));

    var frame = physics.ForRender.Read();
    BeginDrawing();
    ClearBackground(Color.Black);
    Dashboard.Draw(frame, vehicle, viewArea);
    if (!panel.Visible)
        Ui.Text($"physics {frame.PhysicsHz:F0} Hz  overruns {frame.Overruns}   Tab: tuning panel", 8, h - 24, 18, Color.Gray);
    panel.Draw(panelArea, frame, vehicle);

    if (screenshotPath != null && ++frameCount == ScreenshotFrame)
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

Ui.Unload();
CloseWindow();
