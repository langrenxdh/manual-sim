using System.Text.Json;
using Raylib_cs;
using Sim.Core;
using Sim.Training;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>Parking sensor beeps and display, loaded from <c>config/parking.json</c>. Presentation only.</summary>
public sealed record ParkPilotParams
{
    public required bool Enabled { get; init; }
    public required ParkingSensorParams Sensors { get; init; }
    public required double FrontToneHz { get; init; }
    public required double RearToneHz { get; init; }
    public required double BeepS { get; init; }
    /// <summary>Time between beeps at the edge of the range ...</summary>
    public required double SlowestPeriodS { get; init; }
    /// <summary>... shrinking to this just before the tone goes continuous.</summary>
    public required double FastestPeriodS { get; init; }
    /// <summary>Closer than this the tone is continuous: stop.</summary>
    public required double ContinuousBelowM { get; init; }
    /// <summary>0..1.</summary>
    public required double Volume { get; init; }

    public static ParkPilotParams FromJson(string json)
    {
        var p = JsonSerializer.Deserialize<ParkPilotParams>(json, VehicleParams.JsonOptions)
            ?? throw new JsonException("Empty parking file.");
        p.Validate();
        return p;
    }

    public void Validate()
    {
        Sensors.Validate();
        Require(FrontToneHz > 0 && RearToneHz > 0 && BeepS > 0, "tones and beepS must be > 0");
        Require(SlowestPeriodS > FastestPeriodS && FastestPeriodS > BeepS, "beepS < fastestPeriodS < slowestPeriodS");
        Require(ContinuousBelowM >= 0 && ContinuousBelowM < Math.Min(Sensors.FrontRangeM, Sensors.RearRangeM),
            "continuousBelowM must be below both ranges");
        Require(Volume is >= 0 and <= 1, "volume must be in [0, 1]");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}

/// <summary>
/// ParkPilot on the town map: reads the parking sensors each frame, beeps faster as the nearest obstacle
/// gets closer (higher tone in front, lower behind, continuous when very close) and shows the distances
/// on a small car diagram. Silent without an audio device.
/// </summary>
public sealed class ParkPilot : IDisposable
{
    private const int SampleRate = 44100;
    private const double EdgeFadeS = 0.004, ContinuousS = 0.5;

    private static readonly Color Bg = new(10, 10, 14, 200);
    private static readonly Color CarBody = new(200, 202, 206, 255);
    private static readonly Color Far = new(80, 200, 90, 255);
    private static readonly Color Mid = new(250, 200, 40, 255);
    private static readonly Color Near = new(240, 60, 40, 255);

    private Sound _frontBeep, _rearBeep, _frontTone, _rearTone;
    private bool _hasSounds;
    private double _sinceBeepS = double.MaxValue;
    private ParkPilotParams? _built;

    public ParkingSensors.Reading Reading { get; private set; }

    /// <summary>Measures and beeps; call once per rendered frame. Off the town map it is silent.</summary>
    public void Update(ParkPilotParams p, TownMap town, TownCarDef car, in SimState s, double dtS)
    {
        Reading = p.Enabled && s.Steering
            ? ParkingSensors.Measure(town, car, s.WorldX, s.WorldY, s.HeadingRad, s.SpeedMps, s.EngagedGear == Gear.Reverse, p.Sensors)
            : default;
        if (!IsAudioDeviceReady()) return;
        if (!ReferenceEquals(_built, p)) BuildSounds(p);

        bool rearNearer = Reading.RearM is { } r && (Reading.FrontM is not { } f || r <= f);
        double? d = Reading.NearestM;
        Sound beep = rearNearer ? _rearBeep : _frontBeep, tone = rearNearer ? _rearTone : _frontTone;
        Sound otherTone = rearNearer ? _frontTone : _rearTone;
        if (IsSoundPlaying(otherTone)) StopSound(otherTone);
        if (d is not { } dist)
        {
            StopSound(_frontTone);
            StopSound(_rearTone);
            _sinceBeepS = double.MaxValue; // the first beep comes at once
            return;
        }
        if (dist <= p.ContinuousBelowM)
        {
            if (!IsSoundPlaying(tone)) PlaySound(tone);
            return;
        }
        StopSound(tone);
        double range = rearNearer ? p.Sensors.RearRangeM : p.Sensors.FrontRangeM;
        double t = Math.Clamp((dist - p.ContinuousBelowM) / (range - p.ContinuousBelowM), 0, 1);
        double period = p.FastestPeriodS + (p.SlowestPeriodS - p.FastestPeriodS) * t;
        _sinceBeepS = _sinceBeepS == double.MaxValue ? double.MaxValue : _sinceBeepS + dtS;
        if (_sinceBeepS >= period)
        {
            PlaySound(beep);
            _sinceBeepS = 0;
        }
    }

    /// <summary>A small top-down car with a bar at each working end, coloured by distance, bottom centre of the road view.</summary>
    public void Draw(ParkPilotParams p, Rectangle road)
    {
        if (Reading.NearestM == null) return;
        const float carW = 34, carL = 70, bar = 10, gap = 6, pad = 10;
        var box = new Rectangle(road.X + road.Width / 2 - 70, road.Y + road.Height - carL - 2 * (bar + gap) - 2 * pad - 24, 140,
            carL + 2 * (bar + gap) + 2 * pad + 20);
        DrawRectangleRounded(box, 0.15f, 6, Bg);
        float cx = box.X + box.Width / 2, top = box.Y + pad + bar + gap;
        DrawRectangleRounded(new Rectangle(cx - carW / 2, top, carW, carL), 0.3f, 6, CarBody);
        if (Reading.FrontM is { } f) DrawBar(cx, top - gap - bar, f, p.Sensors.FrontRangeM, p.ContinuousBelowM);
        if (Reading.RearM is { } r) DrawBar(cx, top + carL + gap, r, p.Sensors.RearRangeM, p.ContinuousBelowM);
        string text = Reading.NearestM is { } d ? $"{d:F2} m" : "";
        Ui.Centred(text, cx, box.Y + box.Height - pad - 18, 18, Color.White);
    }

    private static void DrawBar(float cx, float y, double distM, double rangeM, double stopM)
    {
        double t = Math.Clamp((distM - stopM) / (rangeM - stopM), 0, 1);
        var colour = t > 0.6 ? Far : t > 0.25 ? Mid : Near;
        float width = 40 + (float)(1 - t) * 60; // grows as the obstacle comes closer
        DrawRectangleRounded(new Rectangle(cx - width / 2, y, width, 10), 0.5f, 4, colour);
    }

    private void BuildSounds(ParkPilotParams p)
    {
        UnloadSounds();
        _frontBeep = Tone(p.FrontToneHz, p.BeepS, p.Volume);
        _rearBeep = Tone(p.RearToneHz, p.BeepS, p.Volume);
        _frontTone = Tone(p.FrontToneHz, ContinuousS, p.Volume);
        _rearTone = Tone(p.RearToneHz, ContinuousS, p.Volume);
        _hasSounds = true;
        _built = p;
    }

    /// <summary>A sine of the given length with short fades at both ends (no clicks), as a raylib sound.</summary>
    private static Sound Tone(double hz, double seconds, double volume)
    {
        int n = (int)(seconds * SampleRate);
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(36 + n * 2);
            w.Write("WAVEfmt "u8);
            w.Write(16);
            w.Write((short)1);              // PCM
            w.Write((short)1);              // mono
            w.Write(SampleRate);
            w.Write(SampleRate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(n * 2);
            int fade = (int)(EdgeFadeS * SampleRate);
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1, Math.Min(i, n - 1 - i) / (double)Math.Max(1, fade));
                w.Write((short)(Math.Sin(2 * Math.PI * hz * i / SampleRate) * env * volume * short.MaxValue));
            }
        }
        var wave = LoadWaveFromMemory(".wav", ms.ToArray());
        var sound = LoadSoundFromWave(wave);
        UnloadWave(wave);
        return sound;
    }

    private void UnloadSounds()
    {
        if (!_hasSounds) return;
        foreach (var s in new[] { _frontBeep, _rearBeep, _frontTone, _rearTone }) UnloadSound(s);
        _hasSounds = false;
    }

    public void Dispose() => UnloadSounds();
}
