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

    /// <summary>Speaks the text in the UI language; a Chinese voice is used when one is installed.</summary>
    public void Say(string text)
    {
        if (_synth == null) return;
        _synth.SpeakAsyncCancelAll();
        try
        {
            var culture = new System.Globalization.CultureInfo(Tr.Chinese ? "zh-CN" : "en-US");
            if (_synth.GetInstalledVoices(culture).FirstOrDefault(v => v.Enabled) is { } voice)
                _synth.SelectVoice(voice.VoiceInfo.Name);
        }
        catch (Exception) // no voice for the language: keep the current one
        {
        }
        _synth.SpeakAsync(Tr.T(text));
    }

    public void Dispose() => _synth?.Dispose();
}
