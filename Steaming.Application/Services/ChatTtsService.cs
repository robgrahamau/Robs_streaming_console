using Steaming.Core;
using Steaming.Core.Ipc;
using Steaming.Core.Models;
using Steaming.Core.Services;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Devices.Enumeration;
using Windows.Media.Core;
using Windows.Media.Playback;
using Steaming.Application.Services.Tts;

namespace Steaming.Application.Services;

// Speaks incoming chat/alert events aloud. Synthesis is delegated to a pluggable ITtsBackend:
// WinRT system voices by default, or the optional Kokoro ONNX engine when selected. This class
// owns the queue, filtering and playback (device routing + watchdog) — not the voice generation.
public sealed class ChatTtsService(EventBus bus, AppSettings settings, WinRtTtsBackend winrt, KokoroTtsBackend kokoro) : IDisposable
{
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;
    private bool _started;
    private bool _warnedKokoroFallback;

    // WPF stores the voice as TtsVoiceName; WinUI stores its WinRT display name in
    // TtsVoiceNameWinUI. The host app overrides this to read the right setting.
    public Func<string?>? VoiceNameProvider { get; set; }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _worker = Task.Run(ProcessQueueAsync);
        bus.Subscribe(OnEventAsync);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _signal.Release(); } catch { }
        try { _worker?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
        _signal.Dispose();
    }

    private Task OnEventAsync(StreamEvent evt)
    {
        if (evt.Type != EventType.Chat)
        {
            // Alert TTS — speak follows/subs/bits/raids/redemptions aloud.
            if (settings.EnableAlertTts)
            {
                var alertText = BuildAlertSpeech(evt);
                if (!string.IsNullOrWhiteSpace(alertText))
                {
                    _queue.Enqueue(alertText);
                    _signal.Release();
                }
            }
            return Task.CompletedTask;
        }

        if (!settings.EnableChatTts)
            return Task.CompletedTask;

        if (evt.User.IsBroadcaster)
            return Task.CompletedTask;

        if (IsIgnoredUser(evt.User.Username) || IsIgnoredUser(evt.User.DisplayName))
            return Task.CompletedTask;

        var message = evt.Data.TryGetValue("message", out var raw) ? raw?.ToString()?.Trim() ?? "" : "";
        if (string.IsNullOrWhiteSpace(message))
            return Task.CompletedTask;

        // Never read bot commands (!so, !uptime, ...)
        if (message.StartsWith('!'))
            return Task.CompletedTask;

        var emotes = evt.Data.TryGetValue("emotes", out var em) ? em as List<EmoteSegment> ?? [] : [];
        var spoken = StripEmotes(message, emotes);
        if (string.IsNullOrWhiteSpace(spoken))
            return Task.CompletedTask;

        _queue.Enqueue($"{evt.User.DisplayName} says {spoken}");
        _signal.Release();
        return Task.CompletedTask;
    }

    private static string? BuildAlertSpeech(StreamEvent evt)
    {
        string user = string.IsNullOrWhiteSpace(evt.User.DisplayName) ? "Someone" : evt.User.DisplayName;
        return evt.Type switch
        {
            EventType.Follow                 => $"{user} just followed",
            EventType.Subscribe              => $"{user} just subscribed",
            EventType.GiftSubscribe          => $"{user} gifted a subscription",
            EventType.Bits                   => evt.Platform == Platform.YouTube
                ? $"{user} sent {GetStr(evt, "amountDisplay", "a Super Chat")}"
                : $"{user} cheered {GetInt(evt, "bits")} bits",
            EventType.Raid                   => $"{user} is raiding with {GetInt(evt, "viewers")} viewers",
            EventType.ChannelPointRedemption => $"{user} redeemed {GetStr(evt, "rewardTitle", "a reward")}",
            _ => null
        };
    }

    private static int GetInt(StreamEvent evt, string key)
        => evt.Data.TryGetValue(key, out var v) && v != null && int.TryParse(v.ToString(), out var i) ? i : 0;

    private static string GetStr(StreamEvent evt, string key, string fallback = "")
        => evt.Data.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v?.ToString()) ? v!.ToString()! : fallback;

    private async Task ProcessQueueAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }

            if (!_queue.TryDequeue(out var text))
                continue;

            try
            {
                var audio = await SynthesizeAsync(text, _cts.Token);
                if (audio != null)
                    await PlayAsync(audio, _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    // Pick the engine from settings. Kokoro is opt-in; if it's selected but unavailable or fails
    // for this utterance, fall back to WinRT so audio is never silently dropped.
    private async Task<TtsAudio?> SynthesizeAsync(string text, CancellationToken ct)
    {
        var rate = ClampTtsSpeed(settings.TtsSpeed);

        if (string.Equals(settings.TtsEngine, "Kokoro", StringComparison.OrdinalIgnoreCase) && kokoro.IsAvailable)
        {
            var kokoroAudio = await kokoro.SynthesizeAsync(text, rate, settings.KokoroVoiceName, ct);
            if (kokoroAudio != null)
                return kokoroAudio;

            if (!_warnedKokoroFallback)
            {
                _warnedKokoroFallback = true;
                System.Diagnostics.Debug.WriteLine("[ChatTts] Kokoro synthesis unavailable/failed — falling back to WinRT.");
            }
        }

        var winName = VoiceNameProvider?.Invoke() ?? settings.TtsVoiceName;
        return await winrt.SynthesizeAsync(text, rate, winName, ct);
    }

    private async Task PlayAsync(TtsAudio audio, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource();
        using var player = new MediaPlayer();
        if (!string.IsNullOrWhiteSpace(settings.TtsAudioDeviceId))
        {
            try
            {
                var deviceInfo = await DeviceInformation.CreateFromIdAsync(settings.TtsAudioDeviceId);
                player.AudioDevice = deviceInfo;
            }
            catch { }
        }
        player.Source = MediaSource.CreateFromStream(audio.Stream, audio.ContentType);
        player.MediaEnded += (_, _) => tcs.TrySetResult();
        player.MediaFailed += (_, _) => tcs.TrySetResult();
        player.Play();
        // MediaEnded/MediaFailed are not guaranteed to fire (output device removed mid-playback,
        // WinRT playback stalls). Without a deadline one bad playback wedges this loop forever and
        // TTS silently stops for the rest of the session.
        var finished = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(90), ct));
        if (finished != tcs.Task)
        {
            try { player.Pause(); } catch { }
            tcs.TrySetResult();
        }
    }

    private static double ClampTtsSpeed(double speed) => Math.Clamp(speed, 0.5, 6.0);

    private bool IsIgnoredUser(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(settings.TtsIgnoredUsers))
            return false;
        foreach (var entry in settings.TtsIgnoredUsers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (string.Equals(entry, name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string StripEmotes(string message, List<EmoteSegment> emotes)
    {
        if (emotes.Count == 0) return message;

        var sb = new StringBuilder();
        int pos = 0;
        foreach (var emote in emotes.OrderBy(e => e.Start))
        {
            int start = Math.Clamp(emote.Start, pos, message.Length);
            if (start > pos)
                sb.Append(message, pos, start - pos);
            pos = Math.Min(emote.End + 1, message.Length);
        }
        if (pos < message.Length)
            sb.Append(message, pos, message.Length - pos);

        return Regex.Replace(sb.ToString().Trim(), @"\s{2,}", " ");
    }
}
