using Windows.Storage.Streams;

namespace Steaming.Application.Services.Tts;

// A produced utterance, ready to hand to MediaPlayer (MediaSource.CreateFromStream).
// Both backends return a WinRT IRandomAccessStream so ChatTtsService's existing playback,
// device-routing and watchdog path is untouched.
public sealed record TtsAudio(IRandomAccessStream Stream, string ContentType);

// Pluggable text-to-speech engine. Implementations: WinRtTtsBackend (default, system voices),
// KokoroTtsBackend (optional, local ONNX neural voices).
public interface ITtsBackend : IDisposable
{
    // Stable identifier matching AppSettings.TtsEngine ("WinRt", "Kokoro").
    string Id { get; }

    // True when this backend can actually produce audio right now (assets present, etc.).
    bool IsAvailable { get; }

    // Synthesize text to a playable stream. Returns null when synthesis cannot be performed
    // (e.g. Kokoro assets missing) so the caller can fall back to another backend.
    Task<TtsAudio?> SynthesizeAsync(string text, double rate, string? voiceName, CancellationToken ct);
}
