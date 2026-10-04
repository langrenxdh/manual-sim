using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace Sim.App;

/// <summary>
/// Real-time engine sound (docs/design.md, 反馈通道 / 声音). Runs in raylib's audio callback, reads
/// the newest physics frame from its own <see cref="LatestValue{T}"/> and never blocks the physics.
/// Harmonics of the firing frequency carry the tone; lugging adds irregular per-firing amplitude
/// modulation; boost adds filtered noise; the exhaust adds firing-pulsed noise under load; sliding tyres
/// squeal; the starter adds a buzz.
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
    private const double TestToneGain = 0.5;
    private const int CompensatedHarmonics = 2;

    private static EngineSound? _instance;

    private readonly LatestValue<Frame> _source;
    private SoundParams _params;
    private AudioStream _stream;

    // Synthesis state, touched only by the audio thread.
    private double _phase, _starterPhase, _rpm;
    private double _loudness, _turbo, _starter, _shudder, _load;
    private double _lugRandom, _noiseLowPass;
    private double _rasp, _raspLowPass, _squeal, _squealLow, _squealBand, _squealPhase, _squealWobble;
    private uint _rng = 0x9E3779B9;
    private double[] _harmonicScale = [];
    private double _testPhase, _testLevel, _lastTestHz;
    private double _speakerLowCutHz, _testToneHz;

    /// <summary>Speaker low-frequency limit from first-time setup; 0 = no compensation.</summary>
    public double SpeakerLowCutHz
    {
        get => Volatile.Read(ref _speakerLowCutHz);
        set => Volatile.Write(ref _speakerLowCutHz, value);
    }

    /// <summary>When above 0, a plain sine at this frequency replaces the engine (speaker sweep).</summary>
    public double TestToneHz
    {
        get => Volatile.Read(ref _testToneHz);
        set => Volatile.Write(ref _testToneHz, value);
    }

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
        double testHz = TestToneHz;
        if (testHz > 0 || _testLevel > 0)
        {
            RenderTestTone(testHz, p, output);
            return;
        }
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
        double raspAlpha = 1 - Math.Exp(-2 * Math.PI * p.ExhaustRaspCutoffHz / SampleRate);
        double raspTarget = p.ExhaustRaspGain * (s.Firing ? 1 : 0) * audible;
        double slipMps = Math.Abs(s.DrivenWheelSpeedMps - s.SpeedMps);
        double squealTarget = s.WheelSlip ? p.SquealGain * Math.Clamp(slipMps / p.SquealFullSlipMps, 0.3, 1) : 0;
        double squealAlpha = 1 - Math.Exp(-2 * Math.PI * p.SquealHz * 1.5 / SampleRate);
        double squealHighAlpha = 1 - Math.Exp(-2 * Math.PI * p.SquealHz * 0.6 / SampleRate);

        var harmonics = p.Harmonics;
        double gainSum = 0;
        foreach (var h in harmonics) gainSum += h[1];
        double norm = gainSum > 0 ? 1 / gainSum : 0;
        double firingsPerRev = s.FiringsPerRev > 0 ? s.FiringsPerRev : 2;
        ScaleForSpeaker(harmonics, rpmEnd / 60 * firingsPerRev, p);

        for (int i = 0; i < output.Length; i++)
        {
            double rpm = rpmStart + (rpmEnd - rpmStart) * (i + 1) / output.Length;
            double firingHz = rpm / 60 * firingsPerRev;
            double before = _phase;
            _phase += firingHz / SampleRate;
            if (Math.Floor(_phase) != Math.Floor(before)) _lugRandom = NextUnit(); // new firing event
            if (_phase >= PhaseWrapCycles) _phase -= PhaseWrapCycles;

            _loudness += (loudTarget - _loudness) * smooth;
            _turbo += (turboTarget - _turbo) * smooth;
            _starter += (starterTarget - _starter) * smooth;
            _shudder += (shudderTarget - _shudder) * smooth;
            _load += (loadTarget - _load) * smooth;
            _rasp += (raspTarget - _rasp) * smooth;
            _squeal += (squealTarget - _squeal) * smooth;

            // Lugging: each firing pulses the upper harmonics by a random amount.
            double withinFiring = _phase - Math.Floor(_phase);
            double lug = 1 - p.LugModulationDepth * _shudder * _lugRandom * (0.5 + 0.5 * Math.Cos(2 * Math.PI * withinFiring));

            double tone = 0;
            for (int k = 0; k < harmonics.Length; k++)
            {
                var h = harmonics[k];
                double order = h[0];
                double gain = h[1] * _harmonicScale[k];
                if (order > 2) gain *= 1 + p.LoadBrightness * _load * (order - 2) / (BrightnessFullOrder - 2);
                if (order >= p.LugMinOrder) gain *= lug;
                tone += gain * Math.Sin(2 * Math.PI * order * _phase);
            }

            _noiseLowPass += (NextSigned() - _noiseLowPass) * noiseAlpha;

            _starterPhase += p.StarterFrequencyHz / SampleRate;
            _starterPhase -= Math.Floor(_starterPhase);
            double starter = Math.Sin(2 * Math.PI * _starterPhase) + StarterThirdHarmonic * Math.Sin(6 * Math.PI * _starterPhase);

            // Exhaust: low-passed noise, pulsed by each firing and growing with load.
            _raspLowPass += (NextSigned() - _raspLowPass) * raspAlpha;
            double pulse = 0.5 + 0.5 * Math.Cos(2 * Math.PI * withinFiring);
            double rasp = _rasp * (0.3 + 0.7 * _load) * pulse * pulse * _raspLowPass;

            // Tyre squeal: a wavering tone around squealHz inside band-passed noise.
            double squeal = 0;
            if (_squeal > 1e-5)
            {
                _squealWobble += (NextSigned() - _squealWobble) * 0.0005;
                _squealPhase += p.SquealHz * (1 + 0.08 * _squealWobble) / SampleRate;
                _squealPhase -= Math.Floor(_squealPhase);
                double n = NextSigned();
                _squealLow += (n - _squealLow) * squealAlpha;
                _squealBand += (_squealLow - _squealBand) * squealHighAlpha;
                squeal = _squeal * (0.6 * Math.Sin(2 * Math.PI * _squealPhase) + 0.8 * (_squealLow - _squealBand));
            }

            double sample = p.MasterGain * (_loudness * tone * norm + _turbo * _noiseLowPass + _starter * starter + rasp) + squeal;
            output[i] = (float)Math.Tanh(sample);
        }
        _rpm = rpmEnd;
    }

    /// <summary>
    /// Speakers cannot play below their limit: turn those harmonics down and give part of the removed
    /// energy to the next harmonics above the limit (design doc, Feedback channels / Sound).
    /// </summary>
    private void ScaleForSpeaker(double[][] harmonics, double firingHz, SoundParams p)
    {
        if (_harmonicScale.Length != harmonics.Length) _harmonicScale = new double[harmonics.Length];
        double cut = SpeakerLowCutHz, removed = 0;
        for (int k = 0; k < harmonics.Length; k++)
        {
            bool below = cut > 0 && harmonics[k][0] * firingHz < cut;
            _harmonicScale[k] = below ? p.BelowSpeakerGain : 1;
            if (below) removed += harmonics[k][1] * (1 - p.BelowSpeakerGain);
        }
        int given = 0;
        for (int k = 0; k < harmonics.Length && given < CompensatedHarmonics && removed > 0; k++)
        {
            if (harmonics[k][0] * firingHz < cut || harmonics[k][1] <= 0) continue;
            _harmonicScale[k] += removed * p.LowCutCompensation / CompensatedHarmonics / harmonics[k][1];
            given++;
        }
    }

    private void RenderTestTone(double hz, SoundParams p, Span<float> output)
    {
        double target = hz > 0 ? TestToneGain : 0;
        if (hz > 0) _lastTestHz = hz;
        else hz = _lastTestHz; // fade out at the last frequency, not as a DC step
        double smooth = 1 - Math.Exp(-1 / (SampleRate * p.ParameterSmoothingS));
        for (int i = 0; i < output.Length; i++)
        {
            _testLevel += (target - _testLevel) * smooth;
            if (target == 0 && _testLevel < 1e-4) _testLevel = 0;
            _testPhase += hz / SampleRate;
            _testPhase -= Math.Floor(_testPhase);
            output[i] = (float)(_testLevel * Math.Sin(2 * Math.PI * _testPhase));
        }
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
