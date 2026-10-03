using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using Raylib_cs;
using Sim.Core;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>
/// Live tuning panel (Tab). Shows every value of every <see cref="TunableDocument"/> as a tree,
/// built from the JSON itself so new parameters appear without code changes (docs/engineering-rules.en.md hard rule 3).
/// Keys: Up/Down select, Left/Right change (Shift x10, Ctrl x0.1), Enter expand/toggle, Ctrl+S save.
/// </summary>
public sealed class TuningPanel
{
    public const float Width = 560;
    private const float RowHeight = 24;
    private const float TextSize = 19;
    private const float IndentPx = 18;
    private const int PageRows = 15;
    // Value steps: relative for magnitudes >= 1, absolute below (so 0 and small fractions still move).
    private const double RelativeStep = 0.01;
    private const double AbsoluteStep = 0.01;
    private const double CoarseFactor = 10;
    private const double FineFactor = 0.1;
    private const int SignificantDigits = 6;

    private enum Kind { Document, Group, Real, Integer, Bool, Text, CurvePoint }

    private sealed record Row(string Path, int Depth, string Label, Kind Kind, TunableDocument Doc,
        JsonNode? Container, string? Key, int Index);

    private static readonly Color Bg = new(24, 24, 30, 255);
    private static readonly Color Fg = new(220, 220, 228, 255);
    private static readonly Color Muted = new(130, 130, 145, 255);
    private static readonly Color Highlight = new(60, 90, 140, 255);
    private static readonly Color ErrorColour = new(255, 110, 90, 255);
    private static readonly Color Good = new(110, 210, 130, 255);

    private readonly List<TunableDocument> _docs;
    private readonly Action<string, string> _save;
    private readonly HashSet<string> _expanded = [];
    private List<Row> _rows = [];
    private int _selected;
    private int _scroll;
    private string? _status;

    public bool Visible { get; set; }

    /// <summary>Extra status lines under the physics readouts (force feedback, telemetry).</summary>
    public IReadOnlyList<string> ExtraReadouts { get; set; } = [];

    /// <param name="save">Writes (file name, JSON text) to config/.</param>
    public TuningPanel(IReadOnlyList<TunableDocument> docs, Action<string, string> save)
    {
        _docs = [.. docs];
        _save = save;
        foreach (var d in docs) _expanded.Add(d.Title);
        Rebuild();
    }

    /// <summary>Swaps the document with the same title (e.g. the Vehicle document when the car changes).</summary>
    public void Replace(TunableDocument doc)
    {
        int i = _docs.FindIndex(d => d.Title == doc.Title);
        if (i < 0) throw new ArgumentException($"No panel document titled \"{doc.Title}\".");
        _docs[i] = doc;
        Rebuild();
    }

    public void Update(Rectangle area, Rectangle listArea)
    {
        if (!Visible) return;
        bool ctrl = IsKeyDown(KeyboardKey.LeftControl) || IsKeyDown(KeyboardKey.RightControl);
        bool shift = IsKeyDown(KeyboardKey.LeftShift) || IsKeyDown(KeyboardKey.RightShift);

        if (ctrl && IsKeyPressed(KeyboardKey.S)) Save();
        if (Pressed(KeyboardKey.Down)) _selected++;
        if (Pressed(KeyboardKey.Up)) _selected--;
        if (Pressed(KeyboardKey.PageDown)) _selected += PageRows;
        if (Pressed(KeyboardKey.PageUp)) _selected -= PageRows;
        _selected = Math.Clamp(_selected, 0, _rows.Count - 1);

        double factor = shift ? CoarseFactor : ctrl ? FineFactor : 1;
        if (Pressed(KeyboardKey.Right)) Adjust(_rows[_selected], +1, factor);
        if (Pressed(KeyboardKey.Left)) Adjust(_rows[_selected], -1, factor);
        if (IsKeyPressed(KeyboardKey.Enter) || IsKeyPressed(KeyboardKey.KpEnter)) Activate(_rows[_selected]);

        var mouse = GetMousePosition();
        if (CheckCollisionPointRec(mouse, area))
            _scroll -= (int)GetMouseWheelMove() * 3;
        if (IsMouseButtonPressed(MouseButton.Left) && CheckCollisionPointRec(mouse, listArea))
        {
            int hit = _scroll + (int)((mouse.Y - listArea.Y) / RowHeight);
            if (hit >= 0 && hit < _rows.Count)
            {
                bool again = hit == _selected;
                _selected = hit;
                if (again || _rows[hit].Kind is Kind.Document or Kind.Group) Activate(_rows[hit]);
            }
        }

        int visible = Math.Max(1, (int)(listArea.Height / RowHeight));
        if (_selected < _scroll) _scroll = _selected;
        if (_selected >= _scroll + visible) _scroll = _selected - visible + 1;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _rows.Count - visible));
    }

    /// <summary>Area left for the list after the live readouts.</summary>
    public static Rectangle ListArea(Rectangle area) =>
        new(area.X, area.Y + ReadoutHeight, area.Width, area.Height - ReadoutHeight - FooterHeight);

    private const float ReadoutHeight = 13 * 23 + 16;
    private const float FooterHeight = 3 * 22 + 8;

    public void Draw(Rectangle area, in Frame f, VehicleParams p)
    {
        if (!Visible) return;
        DrawRectangleRec(area, Bg);
        DrawReadouts(area, f, p);

        var list = ListArea(area);
        BeginScissorMode((int)list.X, (int)list.Y, (int)list.Width, (int)list.Height);
        int visible = (int)(list.Height / RowHeight) + 1;
        for (int i = _scroll; i < Math.Min(_rows.Count, _scroll + visible); i++)
        {
            float y = list.Y + (i - _scroll) * RowHeight;
            if (i == _selected) DrawRectangle((int)list.X, (int)y, (int)list.Width, (int)RowHeight, Highlight);
            DrawRow(_rows[i], list.X + 10, y, list.Width - 20);
        }
        EndScissorMode();

        float fy = area.Y + area.Height - FooterHeight + 4;
        var error = _docs.Select(d => d.Error).FirstOrDefault(e => e != null);
        if (error != null) Ui.Text(error, area.X + 10, fy, 17, ErrorColour);
        else if (_status != null) Ui.Text(_status, area.X + 10, fy, 17, Good);
        Ui.Text("Up/Down select   Left/Right change (Shift x10, Ctrl x0.1)", area.X + 10, fy + 22, 16, Muted);
        Ui.Text("Enter expand/toggle   Ctrl+S save   R reset car   Tab hide", area.X + 10, fy + 44, 16, Muted);
    }

    private void DrawReadouts(Rectangle area, in Frame f, VehicleParams p)
    {
        var s = f.State;
        var i = f.Input;
        float x = area.X + 10, y = area.Y + 8;
        void Line(string text, Color? c = null)
        {
            Ui.Text(text, x, y, TextSize, c ?? Fg);
            y += 23;
        }

        Line(f.InputError ?? (i.Connected ? f.DeviceName ?? "wheel" : "NO WHEEL"), i.Connected ? Good : ErrorColour);
        Line($"pedal raw>filtered  C {i.ClutchNormalised:F2}>{i.Input.Clutch:F2}  " +
             $"T {i.ThrottleNormalised:F2}>{i.Input.Throttle:F2}  B {i.BrakeNormalised:F2}>{i.Input.Brake:F2}");
        Line($"lever {i.Input.Shifter}   engaged {s.EngagedGear}{(s.Grinding ? "  GRINDING" : "")}   " +
             $"handbrake {(i.Input.Handbrake ? "on" : "off")}   starter {(i.Input.Starter ? "on" : "off")}");
        Line($"clutch c {s.ClutchEngagement:F3}   {(s.ClutchLocked ? "locked" : "slipping")}   " +
             $"slip {s.ClutchSlipRpm:F0} rpm   {s.ClutchTorqueNm:F0} Nm");
        Line($"engine {s.EngineRpm:F0} rpm   stall margin {s.StallMarginRpm:F0} rpm   " +
             $"{(s.Firing ? "firing" : "not firing")}", s.StallMarginRpm < 0 ? ErrorColour : null);
        Line($"idle control {s.IdleControlUsage * 100:F0} % of {p.IdleControl.MaxTorqueNm:F0} Nm   " +
             $"throttle {s.Throttle * 100:F0} %");
        Line($"combustion {s.CombustionTorqueNm:F0} Nm   friction {s.FrictionTorqueNm:F0} Nm   boost {s.Boost:F2}");
        Line($"shudder {s.ShudderIntensity:F2}   clutch {s.ClutchTempC:F0} C x{s.ClutchFrictionFactor:F2}   engine {s.EngineTempC:F0} C   idle target {s.IdleTargetRpm:F0}{(s.AirCon ? "   A/C" : "")}");
        Line($"speed {s.SpeedKmh:F1} km/h   accel {s.AccelerationMps2 / p.Environment.GravityMps2:F2} g   " +
             $"grade {s.Grade * 100:F1} %");
        Line($"hill hold {s.HillHold}   {s.HillHoldRemainingS:F1} s   {s.HillHoldForceN:F0} N");
        Line($"physics {f.PhysicsHz:F0} Hz   overruns {f.Overruns}   t {s.TimeS:F1} s");
        foreach (var extra in ExtraReadouts) Line(extra);
        DrawLine((int)area.X, (int)(area.Y + ReadoutHeight - 6), (int)(area.X + area.Width),
            (int)(area.Y + ReadoutHeight - 6), Muted);
    }

    private void DrawRow(Row r, float x, float y, float width)
    {
        float lx = x + r.Depth * IndentPx;
        float ty = y + (RowHeight - TextSize) / 2;
        switch (r.Kind)
        {
            case Kind.Document:
                string dirty = r.Doc.Dirty ? "  (unsaved)" : "";
                string file = r.Doc.FileName ?? "not saved";
                Ui.Text($"{Arrow(r)} {r.Label}  [{file}]{dirty}", lx, ty, TextSize, r.Doc.Dirty ? Good : Fg);
                return;
            case Kind.Group:
                Ui.Text($"{Arrow(r)} {r.Label}", lx, ty, TextSize, Fg);
                return;
        }
        Ui.Text(r.Label, lx, ty, TextSize, Muted);
        string value = TunableDocument.Get(r.Container!, r.Key, r.Index)?.ToJsonString() ?? "null";
        Ui.Text(value, x + width - Ui.Width(value, TextSize), ty, TextSize, Fg);
    }

    private string Arrow(Row r) => _expanded.Contains(r.Path) ? "v" : ">";

    private void Activate(Row r)
    {
        switch (r.Kind)
        {
            case Kind.Document or Kind.Group:
                if (!_expanded.Remove(r.Path)) _expanded.Add(r.Path);
                Rebuild();
                break;
            case Kind.Bool:
                Adjust(r, +1, 1);
                break;
        }
    }

    private void Adjust(Row r, int direction, double factor)
    {
        switch (r.Kind)
        {
            case Kind.Document or Kind.Group:
                if (direction > 0) _expanded.Add(r.Path);
                else _expanded.Remove(r.Path);
                Rebuild();
                return;
            case Kind.Bool:
                bool b = TunableDocument.Get(r.Container!, r.Key, r.Index)!.GetValue<bool>();
                r.Doc.TrySet(r.Container!, r.Key, r.Index, JsonValue.Create(!b));
                return;
            case Kind.Integer:
                long n = TunableDocument.Get(r.Container!, r.Key, r.Index)!.GetValue<long>();
                long stepN = Math.Max(1, (long)Math.Round(factor));
                r.Doc.TrySet(r.Container!, r.Key, r.Index, JsonValue.Create(n + direction * stepN));
                return;
            case Kind.Real or Kind.CurvePoint:
                double v = TunableDocument.Get(r.Container!, r.Key, r.Index)!.GetValue<double>();
                double step = (Math.Abs(v) >= 1 ? Math.Abs(v) * RelativeStep : AbsoluteStep) * factor;
                r.Doc.TrySet(r.Container!, r.Key, r.Index, JsonValue.Create(Tidy(v + direction * step)));
                return;
        }
    }

    private void Save()
    {
        var saved = new List<string>();
        foreach (var d in _docs.Where(d => d.FileName != null && d.Dirty))
        {
            _save(d.FileName!, JsonLayout.Write(d.Root));
            d.Dirty = false;
            saved.Add(d.FileName!);
        }
        _status = saved.Count == 0 ? "nothing to save"
            : $"saved {string.Join(", ", saved)}" +
              (saved.Contains(ConfigFiles.VehicleFile) ? " - run dotnet test (T1-T5)" : "");
    }

    private void Rebuild()
    {
        var rows = new List<Row>();
        foreach (var d in _docs)
        {
            rows.Add(new Row(d.Title, 0, d.Title, Kind.Document, d, null, null, 0));
            if (_expanded.Contains(d.Title)) AddObject(rows, d, d.Root, d.RootType, d.Title, 1);
        }
        _rows = rows;
        _selected = Math.Clamp(_selected, 0, _rows.Count - 1);
    }

    private void AddObject(List<Row> rows, TunableDocument d, JsonObject obj, Type? type, string path, int depth)
    {
        foreach (var (key, node) in obj)
        {
            string p = path + "/" + key;
            var propType = PropertyType(type, key);
            switch (node)
            {
                case JsonObject child:
                    rows.Add(new Row(p, depth, key, Kind.Group, d, obj, key, 0));
                    if (_expanded.Contains(p)) AddObject(rows, d, child, propType, p, depth + 1);
                    break;
                case JsonArray arr:
                    rows.Add(new Row(p, depth, key, Kind.Group, d, obj, key, 0));
                    if (_expanded.Contains(p)) AddArray(rows, d, arr, propType, p, depth + 1);
                    break;
                case JsonValue value:
                    rows.Add(new Row(p, depth, key, ValueKind(value, propType), d, obj, key, 0));
                    break;
            }
        }
    }

    private void AddArray(List<Row> rows, TunableDocument d, JsonArray arr, Type? type, string path, int depth)
    {
        var elementType = type?.IsArray == true ? type.GetElementType() : null;
        for (int i = 0; i < arr.Count; i++)
        {
            if (arr[i] is JsonObject obj)
            {
                // Arrays of records (exercises, scoring rows): one group each, named by its id or metric.
                string p = $"{path}/{i}";
                string label = (obj["id"] ?? obj["metric"])?.GetValue<string>() ?? $"[{i}]";
                rows.Add(new Row(p, depth, label, Kind.Group, d, arr, null, i));
                if (_expanded.Contains(p)) AddObject(rows, d, obj, elementType, p, depth + 1);
            }
            else if (arr[i] is JsonArray pair && pair.Count == 2)
            {
                // [x, y] pairs (curves, harmonics): x is the key, kept fixed so curves stay increasing; y is tuned.
                string label = type == typeof(Curve) ? $"at {pair[0]!.ToJsonString()}" : $"[{pair[0]!.ToJsonString()}]";
                rows.Add(new Row($"{path}/{i}", depth, label, Kind.CurvePoint, d, pair, null, 1));
            }
            else if (arr[i] is JsonValue v)
            {
                rows.Add(new Row($"{path}/{i}", depth, $"[{i}]", ValueKind(v, elementType), d, arr, null, i));
            }
        }
    }

    private static Kind ValueKind(JsonValue value, Type? clrType)
    {
        var t = Nullable.GetUnderlyingType(clrType ?? typeof(object)) ?? clrType;
        if (t == typeof(bool)) return Kind.Bool;
        if (t == typeof(string)) return Kind.Text;
        if (t == typeof(int) || t == typeof(long) || t == typeof(ulong)) return Kind.Integer;
        if (t == typeof(double) || t == typeof(float)) return Kind.Real;
        return value.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => Kind.Bool,
            System.Text.Json.JsonValueKind.Number => Kind.Real,
            _ => Kind.Text,
        };
    }

    private static Type? PropertyType(Type? type, string jsonKey)
    {
        if (type == null || jsonKey.Length == 0) return null;
        string name = char.ToUpperInvariant(jsonKey[0]) + jsonKey[1..];
        return type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.PropertyType;
    }

    /// <summary>Rounds away binary noise so saved files stay readable.</summary>
    private static double Tidy(double v)
    {
        if (v == 0) return 0;
        int digits = SignificantDigits - 1 - (int)Math.Floor(Math.Log10(Math.Abs(v)));
        return Math.Round(v, Math.Clamp(digits, 0, 15));
    }

    private static bool Pressed(KeyboardKey k) => IsKeyPressed(k) || IsKeyPressedRepeat(k);
}
