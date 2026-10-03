using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>
/// Real-time engine sound (docs/design.md, 反馈通道 / 声音). Runs in raylib's audio callback, reads
/// the newest physics frame from its own <see cref="LatestValue{T}"/> and never blocks the physics.
/// Harmonics of the firing frequency carry the tone; lugging adds irregular per-firing amplitude
/// modulation; boost adds filtered noise; the starter adds a buzz.
/// </summary>
public sealed unsafe class EngineSound : IDisposable
{
    public const int SampleRate = 48000;
    private const int BufferFrames = 1024; // ~21 ms
    // Phase is kept in firing cycles and wrapped at a multiple of every small integer order and of 0.5,
    // so harmonic phases stay continuous across the wrap.
    private const double PhaseWrapCycles = 720720;
    // Load brightness reaches its full value at this harmonic order and grows linearly from order 2.
    private const double BrightnessFullOrder = 8;
    private const double StarterThirdHarmonic = 0.3;

    private static EngineSound? _instance;

    private readonly LatestValue<Frame> _source;
    private SoundParams _params;
    private AudioStream _stream;

    // Synthesis state, touched only by the audio thread.
    private double _phase, _starterPhase, _rpm;
    private double _loudness, _turbo, _starter, _shudder, _load;
    private double _lugRandom, _noiseLowPass;
    private uint _rng = 0x9E3779B9;

    /// <summary>May be replaced at any time (tuning panel); read once per audio block.</summary>
    public SoundParams Params
    {
        get => Volatile.Read(ref _params);
        set => Volatile.Write(ref _params, value);
    }

    private EngineSound(LatestValue<Frame> source, SoundParams p)
    {
        _source = source;
        _params = p;
    }

    /// <summary>A synthesiser with no audio device, for rendering offline with <see cref="Render"/>.</summary>
    public static EngineSound Offline(SoundParams p) => new(new LatestValue<Frame>(), p);

    /// <summary>Opens the audio device and starts playing. Returns null when there is no audio device.</summary>
    public static EngineSound? Start(LatestValue<Frame> source, SoundParams p)
    {
        InitAudioDevice();
        if (!IsAudioDeviceReady()) return null;
        var sound = new EngineSound(source, p);
        _instance = sound;
        SetAudioStreamBufferSizeDefault(BufferFrames);
        sound._stream = LoadAudioStream(SampleRate, 32, 1);
        SetAudioStreamCallback(sound._stream, &Callback);
        PlayAudioStream(sound._stream);
        return sound;
    }

    public void Dispose()
    {
        StopAudioStream(_stream);
        UnloadAudioStream(_stream);
        CloseAudioDevice();
        _instance = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Callback(void* buffer, uint frames)
    {
        var output = new Span<float>(buffer, (int)frames);
        try
        {
            if (_instance is { } s) s.Render(s._source.Read(), output);
            else output.Clear();
        }
        catch
        {
            // An exception must not escape into native code; play silence for this block.
            output.Clear();
        }
    }

    /// <summary>Fills one block. Public so the synthesis can be checked offline without a device.</summary>
    public void Render(in Frame f, Span<float> output)
    {
        var p = Params;
        var s = f.State;
        double rpmStart = _rpm, rpmEnd = Math.Max(0, s.EngineRpm);
        double audible = Math.Clamp(rpmEnd / p.AudibleFromRpm, 0, 1);
        double loadTarget = Math.Clamp(s.CombustionTorqueNm / p.LoadReferenceNm, 0, 1);
        double loudTarget = (s.Firing ? 1 : p.NotFiringGain) * (p.IdleLoudness + p.LoadLoudness * loadTarget) * audible;
        double turboTarget = p.TurboGain * Math.Clamp(s.Boost, 0, 1);
        double starterTarget = f.Input.Input.Starter ? p.StarterGain : 0;
        double shudderTarget = Math.Clamp(s.ShudderIntensity, 0, 1);
        double smooth = 1 - Math.Exp(-1 / (SampleRate * p.ParameterSmoothingS));
        double noiseAlpha = 1 - Math.Exp(-2 * Math.PI * p.TurboCutoffHz / SampleRate);

        var harmonics = p.Harmonics;
        double gainSum = 0;
        foreach (var h in harmonics) gainSum += h[1];
        double norm = gainSum > 0 ? 1 / gainSum : 0;

        for (int i = 0; i < output.Length; i++)
        {
            double rpm = rpmStart + (rpmEnd - rpmStart) * (i + 1) / output.Length;
            double firingHz = rpm / 60 * 2;
            double before = _phase;
            _phase += firingHz / SampleRate;
            if (Math.Floor(_phase) != Math.Floor(before)) _lugRandom = NextUnit(); // new firing event
            if (_phase >= PhaseWrapCycles) _phase -= PhaseWrapCycles;

            _loudness += (loudTarget - _loudness) * smooth;
            _turbo += (turboTarget - _turbo) * smooth;
            _starter += (starterTarget - _starter) * smooth;
            _shudder += (shudderTarget - _shudder) * smooth;
            _load += (loadTarget - _load) * smooth;

            // Lugging: each firing pulses the upper harmonics by a random amount.
            double withinFiring = _phase - Math.Floor(_phase);
            double lug = 1 - p.LugModulationDepth * _shudder * _lugRandom * (0.5 + 0.5 * Math.Cos(2 * Math.PI * withinFiring));

            double tone = 0;
            foreach (var h in harmonics)
            {
                double order = h[0];
                double gain = h[1];
                if (order > 2) gain *= 1 + p.LoadBrightness * _load * (order - 2) / (BrightnessFullOrder - 2);
                if (order >= p.LugMinOrder) gain *= lug;
                tone += gain * Math.Sin(2 * Math.PI * order * _phase);
            }

            _noiseLowPass += (NextSigned() - _noiseLowPass) * noiseAlpha;

            _starterPhase += p.StarterFrequencyHz / SampleRate;
            _starterPhase -= Math.Floor(_starterPhase);
            double starter = Math.Sin(2 * Math.PI * _starterPhase) + StarterThirdHarmonic * Math.Sin(6 * Math.PI * _starterPhase);

            double sample = p.MasterGain * (_loudness * tone * norm + _turbo * _noiseLowPass + _starter * starter);
            output[i] = (float)Math.Tanh(sample);
        }
        _rpm = rpmEnd;
    }

    private double NextUnit()
    {
        _rng ^= _rng << 13;
        _rng ^= _rng >> 17;
        _rng ^= _rng << 5;
        return _rng / (double)uint.MaxValue;
    }

    private double NextSigned() => 2 * NextUnit() - 1;
}
