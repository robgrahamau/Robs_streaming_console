using Steaming.Core.Models;

namespace Steaming.Core.Services;

// Subscribes to EventBus and plays per-event sound files configured in AppSettings.
// The actual playback delegate is set by the WPF layer so Core has no WPF dependency.
public class SoundDispatcher
{
    private readonly EventBus _bus;
    private readonly AppSettings _settings;

    // Set by App.xaml.cs to a WPF MediaPlayer wrapper — null = no sound
    public Action<string, float>? PlayFile { get; set; }

    public SoundDispatcher(EventBus bus, AppSettings settings)
    {
        _bus      = bus;
        _settings = settings;
    }

    private bool _started;
    public void Start()
    {
        if (_started) return;
        _started = true;
        _bus.Subscribe(OnEvent);
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _bus.Unsubscribe(OnEvent);
    }

    private Task OnEvent(StreamEvent evt)
    {
        if (PlayFile == null) return Task.CompletedTask;

        EventConfig? directConfig = null;
        string? key = evt.Type switch
        {
            EventType.Follow                 => "Follow",
            EventType.Subscribe              => "Subscribe",
            EventType.GiftSubscribe          => "GiftSubscribe",
            EventType.Bits                   => "Bits",
            EventType.Raid                   => "Raid",
            EventType.ChannelPointRedemption => ResolveRewardSoundConfig(evt, out directConfig),
            EventType.KicksGifted            => "Bits",
            _                                => null
        };
        if (key == null && directConfig == null) return Task.CompletedTask;

        var cfg = directConfig;
        if (cfg == null && key != null)
            _settings.Events.TryGetValue(key, out cfg);

        if (cfg != null &&
            cfg.Enabled &&
            !string.IsNullOrWhiteSpace(cfg.SoundFile) &&
            File.Exists(cfg.SoundFile))
        {
            PlayFile(cfg.SoundFile, cfg.Volume);
        }

        return Task.CompletedTask;
    }

    private string? ResolveRewardSoundConfig(StreamEvent evt, out EventConfig? directConfig)
    {
        directConfig = null;
        var rewardId = evt.Data.TryGetValue("rewardId", out var idObj) ? idObj?.ToString() ?? "" : "";
        var rewardTitle = evt.Data.TryGetValue("rewardTitle", out var title) ? title?.ToString() ?? "" : "";
        if (CustomAlertMatcher.TryResolveRewardAlert(
            _settings,
            rewardId,
            rewardTitle,
            evt.Platform.ToString(),
            out _,
            out var matchedConfig))
        {
            directConfig = matchedConfig;
            return null;
        }

        return "RewardRedemption";
    }
}
