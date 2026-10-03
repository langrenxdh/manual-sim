using System.Globalization;
using Raylib_cs;
using Sim.Input;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>
/// First-time setup (design doc: hardware table; Feedback channels). Asks for the visible screen
/// width and the viewing distance (field of view), then plays a slow rising sine so the driver can
/// mark where the speakers start to be audible. Opens automatically when config/setup.json is
/// missing; F2 opens it again. Physics keeps running underneath.
/// </summary>
public sealed class SetupScreen
{
    // Logarithmic sweep: equal time per octave, slow enough to react.
    private const double SweepStartHz = 20, SweepEndHz = 250, SweepDurationS = 25;

    private enum Step { ScreenWidth, ViewingDistance, SweepIntro, Sweeping, Summary }

    private static readonly Color Bg = new(14, 14, 18, 245);
    private static readonly Color Fg = new(225, 225, 232, 255);
    private static readonly Color Muted = new(140, 140, 150, 255);
    private static readonly Color Accent = new(255, 170, 40, 255);
    private static readonly Color ErrorColour = new(255, 110, 90, 255);

    private readonly EngineSound? _sound;
    private readonly Action<SetupParams> _save;
    private Step _step;
    private string _entry = "";
    private string? _error;
    private double _widthCm, _distanceCm, _lowCutHz, _sweepT;
    private UiButtons _previousButtons;

    public bool Active { get; private set; }

    public SetupScreen(EngineSound? sound, Action<SetupParams> save)
    {
        _sound = sound;
        _save = save;
    }

    /// <summary>Starts setup, pre-filled with the current values.</summary>
    public void Open(SetupParams current)
    {
        _widthCm = current.ScreenWidthCm;
        _distanceCm = current.ViewingDistanceCm;
        _lowCutHz = current.SpeakerLowCutHz;
        _step = Step.ScreenWidth;
        _entry = Format(_widthCm);
        _error = null;
        Active = true;
    }

    public void Cancel()
    {
        StopTone();
        Active = false;
    }

    public void Update(UiButtons buttons, double dtS)
    {
        if (!Active) return;
        bool wheelPressed = (buttons.TeachingMode && !_previousButtons.TeachingMode)
                            || (buttons.Replay && !_previousButtons.Replay)
                            || (buttons.HillStart && !_previousButtons.HillStart);
        _previousButtons = buttons;
        bool enter = IsKeyPressed(KeyboardKey.Enter) || IsKeyPressed(KeyboardKey.KpEnter);

        switch (_step)
        {
            case Step.ScreenWidth:
                if (EditNumber(enter, 10, 500, out double w)) { _widthCm = w; _step = Step.ViewingDistance; _entry = Format(_distanceCm); }
                break;
            case Step.ViewingDistance:
                if (EditNumber(enter, 20, 500, out double d)) { _distanceCm = d; _step = Step.SweepIntro; }
                break;
            case Step.SweepIntro:
                if (IsKeyPressed(KeyboardKey.Space) || wheelPressed) { _sweepT = 0; _step = Step.Sweeping; }
                else if (enter) _step = Step.Summary; // keep the previous speaker value
                break;
            case Step.Sweeping:
                _sweepT += dtS;
                double hz = SweepHz(_sweepT);
                if (IsKeyPressed(KeyboardKey.Space) || wheelPressed)
                {
                    _lowCutHz = Math.Round(hz);
                    _error = null;
                    StopTone();
                    _step = Step.Summary;
                }
                else if (_sweepT >= SweepDurationS)
                {
                    _error = $"No tone heard up to {SweepEndHz:F0} Hz - check the volume and try again.";
                    StopTone();
                    _step = Step.SweepIntro;
                }
                else if (_sound != null) _sound.TestToneHz = hz;
                break;
            case Step.Summary:
                if (enter)
                {
                    _save(new SetupParams { ScreenWidthCm = _widthCm, ViewingDistanceCm = _distanceCm, SpeakerLowCutHz = _lowCutHz });
                    Active = false;
                }
                else if (IsKeyPressed(KeyboardKey.S)) _step = Step.SweepIntro;
                break;
        }
    }

    public void Draw(Rectangle area)
    {
        if (!Active) return;
        DrawRectangleRec(area, Bg);
        float x = area.X + 80, y = area.Y + 70;
        Ui.Text("First-time setup", x, y, 40, Fg);
        Ui.Text("Esc cancels; nothing is saved until the last step.", x, y + 50, 20, Muted);
        y += 130;

        switch (_step)
        {
            case Step.ScreenWidth:
                Ui.Text("1/3  Visible width of your screen, in cm (measure the picture, not the frame):", x, y, 26, Fg);
                Entry(x, y + 50, "cm");
                break;
            case Step.ViewingDistance:
                Ui.Text("2/3  Distance from your eyes to the screen when seated at the wheel, in cm:", x, y, 26, Fg);
                Entry(x, y + 50, "cm");
                break;
            case Step.SweepIntro:
                Ui.Text("3/3  Speaker check. A quiet tone will rise slowly from 20 Hz.", x, y, 26, Fg);
                Ui.Text("Press SPACE or a wheel button to start, then again as soon as you hear it.", x, y + 40, 26, Fg);
                Ui.Text($"Enter skips and keeps {_lowCutHz:F0} Hz.", x, y + 80, 22, Muted);
                break;
            case Step.Sweeping:
                Ui.Text("Listening... press SPACE or a wheel button as soon as you hear the tone.", x, y, 26, Fg);
                Ui.Text($"{SweepHz(_sweepT):F0} Hz", x, y + 60, 64, Accent);
                break;
            case Step.Summary:
                Ui.Text($"Screen width {_widthCm:F0} cm, viewing distance {_distanceCm:F0} cm, speakers audible from {_lowCutHz:F0} Hz.", x, y, 26, Fg);
                Ui.Text("Enter saves to config/setup.json.   S repeats the speaker check.", x, y + 50, 22, Muted);
                break;
        }
        if (_error != null) Ui.Text(_error, x, area.Y + area.Height - 80, 22, ErrorColour);
    }

    /// <summary>Typing digits into the entry field; true with the value when Enter accepts it.</summary>
    private bool EditNumber(bool enter, double min, double max, out double value)
    {
        for (int c = GetCharPressed(); c > 0; c = GetCharPressed())
        {
            if ((char.IsAsciiDigit((char)c) || c == '.') && _entry.Length < 6) _entry += (char)c;
        }
        if (IsKeyPressed(KeyboardKey.Backspace) && _entry.Length > 0) _entry = _entry[..^1];

        value = 0;
        if (!enter) return false;
        if (double.TryParse(_entry, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= min && value <= max)
        {
            _error = null;
            return true;
        }
        _error = $"Enter a number between {min:F0} and {max:F0}.";
        return false;
    }

    private void Entry(float x, float y, string unit)
    {
        bool caret = GetTime() % 1 < 0.5;
        Ui.Text(_entry + (caret ? "_" : " ") + "  " + unit, x, y, 48, Accent);
        Ui.Text("Type the number, Backspace to correct, Enter to continue.", x, y + 64, 20, Muted);
    }

    private void StopTone()
    {
        if (_sound != null) _sound.TestToneHz = 0;
    }

    private static double SweepHz(double t) =>
        SweepStartHz * Math.Pow(SweepEndHz / SweepStartHz, Math.Clamp(t / SweepDurationS, 0, 1));

    private static string Format(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
