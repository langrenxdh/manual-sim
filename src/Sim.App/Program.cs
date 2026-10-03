using Raylib_cs;
using Sim.App;
using Sim.Core;
using Sim.Input;
using static Raylib_cs.Raylib;

// M2 drivable prototype (docs/design.md, 开发里程碑). Keys: R reset, F11 fullscreen, Esc quit.
// --screenshot FILE: save a frame after start-up and exit (for checking the visuals without a person).
string? screenshotPath = args.Length == 2 && args[0] == "--screenshot" ? Path.GetFullPath(args[1]) : null;
const int ScreenshotFrame = 90;
int frameCount = 0;
var config = ConfigFiles.Locate();
var vehicle = VehicleParams.FromJson(config.Read(ConfigFiles.VehicleFile));
var input = InputConfig.FromJson(config.Read(ConfigFiles.InputFile));

using var physics = new PhysicsLoop(vehicle, input);

SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.VSyncHint | ConfigFlags.Msaa4xHint);
InitWindow(1600, 900, "Manual Sim - " + vehicle.Name);
SetTargetFPS(60);
Ui.Load();
double grade = 0;

while (!WindowShouldClose())
{
    if (IsKeyPressed(KeyboardKey.R)) physics.Reset(grade);
    if (IsKeyPressed(KeyboardKey.F11)) ToggleBorderlessWindowed();

    var frame = physics.ForRender.Read();
    BeginDrawing();
    ClearBackground(Color.Black);
    Dashboard.Draw(frame, vehicle, new Rectangle(0, 0, GetScreenWidth(), GetScreenHeight()));
    Ui.Text($"physics {frame.PhysicsHz:F0} Hz  overruns {frame.Overruns}", 8, GetScreenHeight() - 24, 18, Color.Gray);
    if (screenshotPath != null && ++frameCount == ScreenshotFrame)
    {
        Rlgl.DrawRenderBatchActive();
        var image = LoadImageFromScreen();
        ExportImage(image, screenshotPath);
        Console.WriteLine($"screenshot: rpm {frame.State.EngineRpm:F0} kmh {frame.State.SpeedKmh:F1} gear {frame.State.EngagedGear} input {frame.Input}");
        UnloadImage(image);
        EndDrawing();
        break;
    }
    EndDrawing();
}

Ui.Unload();
CloseWindow();
