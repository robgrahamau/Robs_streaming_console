using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Steaming.Application.Services;

// Captures microphone audio and exposes continuously-updated audio analysis.
// This remains audio-only, but now produces coarse per-vowel hints instead of
// driving visemes from amplitude plus one brightness scalar.
public sealed class MicCaptureService : IDisposable
{
    private WasapiCapture? _capture;
    private float _amplitude;
    private float _brightness;
    private float _visemeAa;
    private float _visemeIh;
    private float _visemeOu;
    private float _visemeEe;
    private float _visemeOh;
    private bool _disposed;

    public float Amplitude => Volatile.Read(ref _amplitude);
    public float SpectralBrightness => Volatile.Read(ref _brightness);
    public float VisemeAaHint => Volatile.Read(ref _visemeAa);
    public float VisemeIhHint => Volatile.Read(ref _visemeIh);
    public float VisemeOuHint => Volatile.Read(ref _visemeOu);
    public float VisemeEeHint => Volatile.Read(ref _visemeEe);
    public float VisemeOhHint => Volatile.Read(ref _visemeOh);

    public static List<(string Id, string Name)> EnumerateInputDevices()
    {
        var list = new List<(string, string)> { ("", "Default microphone") };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                list.Add((d.ID, d.FriendlyName));
        }
        catch { }
        return list;
    }

    public void Start(string? deviceId)
    {
        Stop();

        try
        {
            MMDevice? device = null;
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    var found = enumerator.GetDevice(deviceId);
                    if (found is { State: DeviceState.Active })
                        device = found;
                }
                catch { }
            }

            _capture = device != null
                ? new WasapiCapture(device)
                : new WasapiCapture();

            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();
        }
        catch
        {
            _capture?.Dispose();
            _capture = null;
        }
    }

    public void Stop()
    {
        if (_capture == null) return;
        try { _capture.StopRecording(); } catch { }
        _capture.DataAvailable -= OnDataAvailable;
        _capture.Dispose();
        _capture = null;

        Volatile.Write(ref _amplitude, 0f);
        Volatile.Write(ref _brightness, 0f);
        Volatile.Write(ref _visemeAa, 0f);
        Volatile.Write(ref _visemeIh, 0f);
        Volatile.Write(ref _visemeOu, 0f);
        Volatile.Write(ref _visemeEe, 0f);
        Volatile.Write(ref _visemeOh, 0f);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        float rms = 0f;
        float brightness = 0f;
        float aa = 0f;
        float ih = 0f;
        float ou = 0f;
        float ee = 0f;
        float oh = 0f;

        try
        {
            int sampleRate = _capture?.WaveFormat.SampleRate ?? 48000;
            int channels = Math.Max(_capture?.WaveFormat.Channels ?? 1, 1);

            if (_capture?.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                AnalyzeFloatBuffer(e.Buffer, e.BytesRecorded, channels, sampleRate,
                    out rms, out brightness, out aa, out ih, out ou, out ee, out oh);
            }
            else
            {
                AnalyzePcm16Buffer(e.Buffer, e.BytesRecorded, channels, sampleRate,
                    out rms, out brightness, out aa, out ih, out ou, out ee, out oh);
            }
        }
        catch { }

        Volatile.Write(ref _amplitude, Math.Clamp(rms * 3f, 0f, 1f));
        Volatile.Write(ref _brightness, brightness);
        SmoothWrite(ref _visemeAa, aa);
        SmoothWrite(ref _visemeIh, ih);
        SmoothWrite(ref _visemeOu, ou);
        SmoothWrite(ref _visemeEe, ee);
        SmoothWrite(ref _visemeOh, oh);
    }

    private static void AnalyzeFloatBuffer(
        byte[] buffer,
        int bytesRecorded,
        int channels,
        int sampleRate,
        out float rms,
        out float brightness,
        out float aa,
        out float ih,
        out float ou,
        out float ee,
        out float oh)
    {
        int sampleCount = bytesRecorded / 4;
        int frameCount = sampleCount / channels;
        var mono = new float[Math.Max(frameCount, 1)];
        float sumSq = 0f;
        float hfSumSq = 0f;
        float prev = 0f;

        for (int i = 0; i < frameCount; i++)
        {
            float s = 0f;
            for (int ch = 0; ch < channels; ch++)
                s += BitConverter.ToSingle(buffer, (i * channels + ch) * 4);
            s /= channels;
            mono[i] = s;
            sumSq += s * s;
            float diff = s - prev;
            hfSumSq += diff * diff;
            prev = s;
        }

        rms = frameCount > 0 ? MathF.Sqrt(sumSq / frameCount) : 0f;
        float hfrms = frameCount > 1 ? MathF.Sqrt(hfSumSq / (frameCount - 1)) : 0f;
        brightness = rms > 0.001f ? Math.Clamp(hfrms / (rms + hfrms), 0f, 1f) : 0f;
        AnalyzeVowels(mono, sampleRate, out aa, out ih, out ou, out ee, out oh);
    }

    private static void AnalyzePcm16Buffer(
        byte[] buffer,
        int bytesRecorded,
        int channels,
        int sampleRate,
        out float rms,
        out float brightness,
        out float aa,
        out float ih,
        out float ou,
        out float ee,
        out float oh)
    {
        int sampleCount = bytesRecorded / 2;
        int frameCount = sampleCount / channels;
        var mono = new float[Math.Max(frameCount, 1)];
        float sumSq = 0f;
        float hfSumSq = 0f;
        float prev = 0f;

        for (int i = 0; i < frameCount; i++)
        {
            float s = 0f;
            for (int ch = 0; ch < channels; ch++)
                s += BitConverter.ToInt16(buffer, (i * channels + ch) * 2) / 32768f;
            s /= channels;
            mono[i] = s;
            sumSq += s * s;
            float diff = s - prev;
            hfSumSq += diff * diff;
            prev = s;
        }

        rms = frameCount > 0 ? MathF.Sqrt(sumSq / frameCount) : 0f;
        float hfrms = frameCount > 1 ? MathF.Sqrt(hfSumSq / (frameCount - 1)) : 0f;
        brightness = rms > 0.001f ? Math.Clamp(hfrms / (rms + hfrms), 0f, 1f) : 0f;
        AnalyzeVowels(mono, sampleRate, out aa, out ih, out ou, out ee, out oh);
    }

    private static void AnalyzeVowels(
        float[] samples,
        int sampleRate,
        out float aa,
        out float ih,
        out float ou,
        out float ee,
        out float oh)
    {
        aa = ih = ou = ee = oh = 0f;
        if (samples.Length < 64 || sampleRate <= 0) return;

        float f350 = GoertzelPower(samples, sampleRate, 350f);
        float f500 = GoertzelPower(samples, sampleRate, 500f);
        float f800 = GoertzelPower(samples, sampleRate, 800f);
        float f900 = GoertzelPower(samples, sampleRate, 900f);
        float f1200 = GoertzelPower(samples, sampleRate, 1200f);
        float f1900 = GoertzelPower(samples, sampleRate, 1900f);
        float f2300 = GoertzelPower(samples, sampleRate, 2300f);

        float total = f350 + f500 + f800 + f900 + f1200 + f1900 + f2300;
        if (total <= 1e-6f) return;

        f350 /= total;
        f500 /= total;
        f800 /= total;
        f900 /= total;
        f1200 /= total;
        f1900 /= total;
        f2300 /= total;

        aa = Math.Clamp(0.58f * f800 + 0.32f * f1200 + 0.20f * f500 - 0.20f * f2300, 0f, 1f);
        ih = Math.Clamp(0.40f * f500 + 0.60f * f1900 + 0.15f * f1200 - 0.10f * f800, 0f, 1f);
        ou = Math.Clamp(0.48f * f350 + 0.38f * f900 + 0.18f * f1200 - 0.10f * f2300, 0f, 1f);
        ee = Math.Clamp(0.28f * f350 + 0.72f * f2300 - 0.18f * f800, 0f, 1f);
        oh = Math.Clamp(0.42f * f500 + 0.46f * f900 + 0.18f * f1200 - 0.15f * f2300, 0f, 1f);

        float max = Math.Max(aa, Math.Max(ih, Math.Max(ou, Math.Max(ee, oh))));
        if (max > 1e-6f)
        {
            aa /= max;
            ih /= max;
            ou /= max;
            ee /= max;
            oh /= max;
        }
    }

    private static float GoertzelPower(float[] samples, int sampleRate, float targetHz)
    {
        int n = samples.Length;
        if (n < 2) return 0f;

        float omega = 2f * MathF.PI * targetHz / sampleRate;
        float coeff = 2f * MathF.Cos(omega);
        float s0 = 0f;
        float s1 = 0f;
        float s2 = 0f;
        for (int i = 0; i < n; i++)
        {
            float window = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (n - 1));
            s0 = samples[i] * window + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        return Math.Max(0f, s1 * s1 + s2 * s2 - coeff * s1 * s2);
    }

    private static void SmoothWrite(ref float field, float target)
    {
        float current = Volatile.Read(ref field);
        Volatile.Write(ref field, current + (target - current) * 0.35f);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
