using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Windows.Storage.Streams;

namespace Steaming.Application.Services.Tts;

// Optional local neural TTS using Kokoro-82M (ONNX). Pipeline:
//   text → espeak-ng IPA (in-process) → Kokoro token ids → ONNX (tokens+style+speed) → float PCM
//   → 24 kHz mono WAV (so ChatTtsService's MediaPlayer playback path is reused unchanged).
// All assets are managed by KokoroAssetService (auto-downloaded). Anything missing/failing →
// SynthesizeAsync returns null and the caller falls back to WinRT.
public sealed class KokoroTtsBackend(KokoroAssetService assets, IPhonemizer phonemizer) : ITtsBackend
{
    private const int SampleRate = 24000;

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly ConcurrentDictionary<string, KokoroVoice> _voiceCache = new();
    private InferenceSession? _session;
    private (string token, string style, string speed)? _inputNames;

    public string Id => "Kokoro";

    // Model + phonemizer are the shared prerequisites; per-voice files are checked at call time.
    public bool IsAvailable => phonemizer.IsAvailable && assets.ModelReady;

    public async Task<TtsAudio?> SynthesizeAsync(string text, double rate, string? voiceName, CancellationToken ct)
    {
        if (!IsAvailable) return null;
        var voice = string.IsNullOrWhiteSpace(voiceName) ? "af_heart" : voiceName!;
        if (!assets.VoiceReady(voice)) return null;

        try
        {
            await EnsureLoadedAsync(ct);
            if (_session == null || _inputNames == null) return null;

            var phonemes = await phonemizer.ToPhonemesAsync(text, "en-us", ct);
            var ids = KokoroTokenizer.Encode(phonemes);
            if (ids.Length == 0) return null;
            if (ids.Any(id => id < 0 || id > KokoroTokenizer.MaxTokenId)) return null;

            var tokens = new long[ids.Length + 2];                 // pad (0) at both ends
            Array.Copy(ids, 0, tokens, 1, ids.Length);

            var kv = _voiceCache.GetOrAdd(voice, v => KokoroVoice.Load(assets.VoicePath(v)));
            var style = kv.GetStyle(ids.Length);
            float speed = (float)Math.Clamp(rate, 0.5, 2.0);

            var (tokenName, styleName, speedName) = _inputNames.Value;
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(tokenName, new DenseTensor<long>(tokens, new[] { 1, tokens.Length })),
                NamedOnnxValue.CreateFromTensor(styleName, new DenseTensor<float>(style, new[] { 1, style.Length })),
                NamedOnnxValue.CreateFromTensor(speedName, new DenseTensor<float>(new[] { speed }, new[] { 1 })),
            };

            var session = _session;
            float[] audio = await Task.Run(() =>
            {
                using var results = session.Run(inputs);
                return results.First().AsTensor<float>().ToArray();
            }, ct);

            if (audio.Length == 0) return null;
            return new TtsAudio(BuildWavStream(audio, SampleRate), "audio/wav");
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }   // degrade to WinRT
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_session != null) return;
        await _loadLock.WaitAsync(ct);
        try
        {
            if (_session != null) return;
            var opts = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
            var session = new InferenceSession(assets.ModelPath, opts);
            _inputNames = ResolveInputNames(session);
            _session = session;
        }
        finally { _loadLock.Release(); }
    }

    // Resolve the three inputs by name, then by type/shape, so we're robust to export naming
    // ("input_ids" vs "tokens", etc.). Throws if it can't find all three.
    private static (string token, string style, string speed) ResolveInputNames(InferenceSession session)
    {
        string? token = null, style = null, speed = null;
        foreach (var (name, _) in session.InputMetadata)
        {
            var l = name.ToLowerInvariant();
            if (token == null && (l.Contains("token") || l.Contains("input_id") || l == "ids")) token = name;
            else if (style == null && (l.Contains("style") || l.Contains("ref") || l.Contains("voice"))) style = name;
            else if (speed == null && (l.Contains("speed") || l.Contains("rate") || l.Contains("duration"))) speed = name;
        }
        if (token == null || style == null || speed == null)
        {
            foreach (var (name, meta) in session.InputMetadata)
            {
                if (name == token || name == style || name == speed) continue;
                bool isInt = meta.ElementType == typeof(long) || meta.ElementType == typeof(int);
                if (token == null && isInt) { token = name; continue; }
                long elems = 1;
                foreach (var d in meta.Dimensions) elems *= d > 0 ? d : 1;
                if (style == null && elems >= 64) { style = name; continue; }
                if (speed == null) speed = name;
            }
        }
        if (token == null || style == null || speed == null)
            throw new InvalidOperationException(
                $"Could not resolve Kokoro ONNX inputs from: {string.Join(", ", session.InputMetadata.Keys)}");
        return (token, style, speed);
    }

    private static IRandomAccessStream BuildWavStream(float[] samples, int sampleRate)
    {
        var wav = EncodeWav(samples, sampleRate);
        var ras = new InMemoryRandomAccessStream();
        using (var dw = new DataWriter(ras))
        {
            dw.WriteBytes(wav);
            dw.StoreAsync().AsTask().GetAwaiter().GetResult();
            dw.FlushAsync().AsTask().GetAwaiter().GetResult();
            dw.DetachStream();
        }
        ras.Seek(0);
        return ras;
    }

    // 16-bit PCM mono WAV.
    private static byte[] EncodeWav(float[] samples, int sampleRate)
    {
        int dataLen = samples.Length * 2;
        var buf = new byte[44 + dataLen];
        using var ms = new MemoryStream(buf);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataLen);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);                 // PCM
        w.Write((short)1);                 // mono
        w.Write(sampleRate);
        w.Write(sampleRate * 2);           // byte rate
        w.Write((short)2);                 // block align
        w.Write((short)16);                // bits per sample
        w.Write("data"u8.ToArray());
        w.Write(dataLen);
        foreach (var f in samples)
            w.Write((short)(Math.Clamp(f, -1f, 1f) * short.MaxValue));
        return buf;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _loadLock.Dispose();
    }
}
