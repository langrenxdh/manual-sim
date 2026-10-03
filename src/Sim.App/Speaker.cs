using System.Speech.Synthesis;

namespace Sim.App;

/// <summary>
/// Speaks teaching-mode cues (M8) with the Windows speech synthesiser. A new cue interrupts the
/// previous one so hints never queue up behind each other. If speech is unavailable the cue text is
/// still shown on screen and nothing else happens.
/// </summary>
public sealed class Speaker : IDisposable
{
    private readonly SpeechSynthesizer? _synth;

    public string? Error { get; }

    public Speaker()
    {
        try
        {
            _synth = new SpeechSynthesizer();
            _synth.SetOutputToDefaultAudioDevice();
            _synth.Rate = 1;
        }
        catch (Exception ex) // speech is optional: any failure just disables it
        {
            _synth = null;
            Error = $"speech unavailable: {ex.Message}";
        }
    }

    public void Say(string text)
    {
        if (_synth == null) return;
        _synth.SpeakAsyncCancelAll();
        _synth.SpeakAsync(text);
    }

    public void Dispose() => _synth?.Dispose();
}
