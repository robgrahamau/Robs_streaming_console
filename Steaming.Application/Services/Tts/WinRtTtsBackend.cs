using Windows.Media.SpeechSynthesis;

namespace Steaming.Application.Services.Tts;

// Default engine. Wraps Windows.Media.SpeechSynthesis (system voices, incl. Narrator natural
// voices). This is the exact logic ChatTtsService used before the backend split — behaviour is
// unchanged. The synthesizer is cached and only recreated when the voice or rate changes.
public sealed class WinRtTtsBackend : ITtsBackend
{
    public string Id => "WinRt";
    public bool IsAvailable => true;   // system speech synthesis is always present on Windows

    private SpeechSynthesizer? _synth;
    private string? _activeName;
    private double _activeRate = -1;

    public async Task<TtsAudio?> SynthesizeAsync(string text, double rate, string? voiceName, CancellationToken ct)
    {
        var wantedRate = Math.Clamp(rate, 0.5, 6.0);   // WinRT SpeakingRate range
        if (_synth == null || voiceName != _activeName || Math.Abs(wantedRate - _activeRate) > 0.0001)
        {
            _synth?.Dispose();
            _synth = CreateSynth(voiceName, wantedRate);
            _activeName = voiceName;
            _activeRate = wantedRate;
        }

        var stream = await _synth.SynthesizeTextToStreamAsync(text).AsTask(ct);
        return new TtsAudio(stream, stream.ContentType);
    }

    private static SpeechSynthesizer CreateSynth(string? voiceName, double speakingRate)
    {
        var synth = new SpeechSynthesizer();
        synth.Options.SpeakingRate = speakingRate;
        if (!string.IsNullOrWhiteSpace(voiceName))
        {
            var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.DisplayName == voiceName);
            if (voice != null)
            {
                synth.Voice = voice;
                return synth;
            }
        }
        // Fall back to system default (respects Time & Language → Speech setting)
        var def = SpeechSynthesizer.DefaultVoice;
        if (def != null) synth.Voice = def;
        return synth;
    }

    public void Dispose() => _synth?.Dispose();
}
