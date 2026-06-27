namespace Steaming.Application.Services.Tts;

// Grapheme-to-phoneme conversion. Kokoro consumes IPA phonemes, not raw text.
// The only implementation (EspeakNgPhonemizer) shells out to espeak-ng.exe as a separate
// process, which keeps the GPL dependency isolated and optional.
public interface IPhonemizer
{
    bool IsAvailable { get; }

    // Returns an IPA phoneme string for the given text. Throws if the phonemizer is unavailable.
    Task<string> ToPhonemesAsync(string text, string lang, CancellationToken ct);
}
