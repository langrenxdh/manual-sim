using System.Text.Json;
using Raylib_cs;
using Sim.Core;
using Sim.Input;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>
/// Car menu (V, M7): picks which vehicle file drives the physics. The list comes from
/// <c>config/cars.json</c>; each car's name is read from its own file.
/// </summary>
public sealed class CarMenu
{
    private const float Width = 560, RowHeight = 44;

    private static readonly Color Bg = new(12, 12, 16, 235);
    private static readonly Color Fg = new(225, 225, 232, 255);
    private static readonly Color Muted = new(140, 140, 150, 255);
    private static readonly Color Highlight = new(60, 90, 140, 255);
    private static readonly Color Good = new(90, 210, 110, 255);

    /// <summary>A car: its vehicle file (physics), its engine sound file and its display name.</summary>
    public sealed record Car(string File, string Sound, string Name);

    private readonly List<Car> _cars = [];
    private int _selected;
    private UiButtons _previous;

    public bool Open { get; set; }
    public string? Error { get; }

    public CarMenu(ConfigFiles config)
    {
        try
        {
            var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            using var list = JsonDocument.Parse(config.Read(ConfigFiles.CarsFile), options);
            foreach (var e in list.RootElement.GetProperty("cars").EnumerateArray())
            {
                // An entry is { "vehicle", "sound" } (M12); a bare file name uses the default sound.
                string file = e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetProperty("vehicle").GetString()!;
                string sound = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("sound", out var s) ? s.GetString()! : ConfigFiles.SoundFile;
                _cars.Add(new Car(file, sound, VehicleParams.FromJson(config.Read(file)).Name));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or KeyNotFoundException)
        {
            Error = $"cars: {ex.Message}";
        }
        if (_cars.Count == 0) _cars.Add(new Car(ConfigFiles.VehicleFile, ConfigFiles.SoundFile, "default"));
    }

    /// <summary>Menu input; returns the chosen car, or null.</summary>
    public Car? Update(UiButtons buttons)
    {
        bool up = IsKeyPressed(KeyboardKey.Up) || (buttons.PadUp && !_previous.PadUp);
        bool down = IsKeyPressed(KeyboardKey.Down) || (buttons.PadDown && !_previous.PadDown);
        bool choose = IsKeyPressed(KeyboardKey.Enter) || IsKeyPressed(KeyboardKey.KpEnter) || (buttons.PadRight && !_previous.PadRight);
        _previous = buttons;
        if (!Open) return null;
        if (up) _selected = (_selected + _cars.Count - 1) % _cars.Count;
        if (down) _selected = (_selected + 1) % _cars.Count;
        if (!choose) return null;
        Open = false;
        return _cars[_selected];
    }

    public void Draw(string currentFile, Rectangle area)
    {
        if (!Open) return;
        float w = Math.Min(Width, area.Width - 16);
        var r = new Rectangle(area.X + (area.Width - w) / 2, area.Y + 40, w, 90 + _cars.Count * RowHeight + 20);
        DrawRectangleRounded(r, 0.04f, 6, Bg);
        Ui.Text("Car", r.X + 24, r.Y + 18, 32, Fg);
        Ui.Text("Up/Down select, Enter start with it, Esc or V close", r.X + 24, r.Y + 58, 17, Muted);
        for (int i = 0; i < _cars.Count; i++)
        {
            float y = r.Y + 90 + i * RowHeight;
            if (i == _selected) DrawRectangle((int)r.X + 12, (int)y, (int)r.Width - 24, (int)RowHeight - 4, Highlight);
            Ui.Text(_cars[i].Name, r.X + 28, y + 8, 22, Fg);
            if (_cars[i].File == currentFile) Ui.Text("current", r.X + r.Width - 110, y + 10, 18, Good);
        }
        if (Error != null) Ui.Text(Error, r.X + 24, r.Y + r.Height - 22, 15, Muted);
    }
}
