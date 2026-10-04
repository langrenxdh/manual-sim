using Raylib_cs;
using Sim.Input;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>
/// "Who is driving?" (M11): opens at start and from the exercise menu. Up/Down (or the D-pad) pick a
/// driver, Enter (or D-pad right) drives as them; the last row adds a new driver by typing a name.
/// Until a driver exists it cannot be closed, so every score has an owner.
/// </summary>
public sealed class DriverPicker
{
    private const float Width = 600, RowHeight = 48, MaxNameLength = 20;

    private static readonly Color Bg = new(12, 12, 16, 240);
    private static readonly Color Fg = new(225, 225, 232, 255);
    private static readonly Color Muted = new(140, 140, 150, 255);
    private static readonly Color Highlight = new(60, 90, 140, 255);
    private static readonly Color Accent = new(255, 170, 40, 255);

    private int _selected;
    private bool _typing;
    private string _entry = "";
    private UiButtons _previous;

    public bool Open { get; private set; }

    public void Show(DriverProfiles profiles, DriverProfiles.Driver? current)
    {
        Open = true;
        _typing = profiles.Drivers.Count == 0;
        _entry = "";
        _selected = current == null ? 0 : Math.Max(0, profiles.Drivers.ToList().FindIndex(d => d.Id == current.Id));
    }

    /// <summary>Handles input; returns the chosen driver (new or existing) once one is picked.</summary>
    public DriverProfiles.Driver? Update(DriverProfiles profiles, UiButtons buttons)
    {
        bool up = IsKeyPressed(KeyboardKey.Up) || (buttons.PadUp && !_previous.PadUp);
        bool down = IsKeyPressed(KeyboardKey.Down) || (buttons.PadDown && !_previous.PadDown);
        bool enter = IsKeyPressed(KeyboardKey.Enter) || IsKeyPressed(KeyboardKey.KpEnter);
        bool choose = enter || (buttons.PadRight && !_previous.PadRight);
        _previous = buttons;
        if (!Open) return null;

        if (_typing)
        {
            for (int c = GetCharPressed(); c > 0; c = GetCharPressed())
                if (c is >= 32 and < 127 && (char.IsLetterOrDigit((char)c) || " -_.'".Contains((char)c)) && _entry.Length < MaxNameLength)
                    _entry += (char)c;
            if (IsKeyPressed(KeyboardKey.Backspace) && _entry.Length > 0) _entry = _entry[..^1];
            if (IsKeyPressed(KeyboardKey.Escape) && profiles.Drivers.Count > 0) _typing = false;
            if (enter && _entry.Trim().Length > 0)
            {
                Open = false;
                return profiles.Add(_entry);
            }
            return null;
        }

        int rows = profiles.Drivers.Count + 1;
        if (up) _selected = (_selected + rows - 1) % rows;
        if (down) _selected = (_selected + 1) % rows;
        if (IsKeyPressed(KeyboardKey.Escape))
        {
            Open = false;
            return null;
        }
        if (!choose) return null;
        if (_selected == profiles.Drivers.Count)
        {
            _typing = true;
            _entry = "";
            return null;
        }
        Open = false;
        return profiles.Drivers[_selected];
    }

    public void Draw(DriverProfiles profiles, Rectangle area)
    {
        if (!Open) return;
        int rows = profiles.Drivers.Count + 1;
        float w = Math.Min(Width, area.Width - 16);
        float h = 120 + (_typing ? 150 : rows * RowHeight) + 30;
        var r = new Rectangle(area.X + (area.Width - w) / 2, area.Y + Math.Max(20, (area.Height - h) / 3), w, h);
        DrawRectangleRounded(r, 0.04f, 6, Bg);
        Ui.Text("Who is driving?", r.X + 24, r.Y + 18, 32, Fg);
        Ui.Text(_typing ? "Type a name, Backspace to correct, Enter to start" : "Up/Down (or D-pad) select, Enter (or D-pad right) drive",
            r.X + 24, r.Y + 60, 17, Muted);
        if (profiles.HasUnclaimedScores)
            Ui.Text("Your existing scores will belong to this first driver.", r.X + 24, r.Y + 84, 17, Accent);

        float y = r.Y + 120;
        if (_typing)
        {
            Ui.Text("New driver", r.X + 28, y, 22, Muted);
            bool caret = GetTime() % 1 < 0.5;
            Ui.Text(_entry + (caret ? "_" : " "), r.X + 28, y + 36, 44, Accent);
            return;
        }
        for (int i = 0; i < rows; i++, y += RowHeight)
        {
            if (i == _selected) DrawRectangle((int)r.X + 12, (int)y, (int)r.Width - 24, (int)RowHeight - 4, Highlight);
            string label = i < profiles.Drivers.Count ? profiles.Drivers[i].Name : "+ New driver";
            Ui.Text(label, r.X + 28, y + 10, 24, i < profiles.Drivers.Count ? Fg : Muted);
        }
    }
}
