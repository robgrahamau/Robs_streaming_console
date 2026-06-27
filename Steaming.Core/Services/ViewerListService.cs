using System.Text.Json;
using Steaming.Core.Models;
using Microsoft.Extensions.Logging;
using Steaming.Core;

namespace Steaming.Core.Services;

public record ViewerInfo(string UserId, string Login, string DisplayName, Platform Platform);

// Merges Twitch chatters from Helix with Kick chat participants observed on the event bus.
public class ViewerListService
{
    private readonly HttpClient _http = new();
    private readonly ILogger<ViewerListService> _logger;
    private readonly EventBus _bus;
    private System.Timers.Timer? _timer;
    private System.Timers.ElapsedEventHandler? _timerHandler;
    private string? _token;
    private string? _clientId;
    private string? _broadcasterId;
    private readonly object _lock = new();
    private Dictionary<string, ViewerInfo> _twitchViewers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (ViewerInfo Info, DateTimeOffset LastSeen)> _kickViewers = new(StringComparer.OrdinalIgnoreCase);
    private List<ViewerInfo> _viewers = [];
    private static readonly TimeSpan KickViewerTtl = TimeSpan.FromMinutes(20);

    public event Action<IReadOnlyList<ViewerInfo>>? ViewersUpdated;
    public IReadOnlyList<ViewerInfo> Current => _viewers;
    public bool IsConfigured => !string.IsNullOrEmpty(_token) || _kickViewers.Count > 0;

    public ViewerListService(EventBus bus, ILogger<ViewerListService> logger)
    {
        _bus = bus;
        _logger = logger;
    }

    public void Configure(string token, string clientId, string broadcasterId)
    {
        _token         = token;
        _clientId      = clientId;
        _broadcasterId = broadcasterId;
    }

    public void Start()
    {
        Stop();
        _bus.Subscribe(OnEventAsync);
        PublishMergedViewers();
        _ = RefreshAsync();
        _timerHandler = async (_, _) =>
        {
            PruneKickViewers();
            await RefreshAsync();
        };
        _timer = new System.Timers.Timer(30_000) { AutoReset = true };
        _timer.Elapsed += _timerHandler;
        _timer.Start();
    }

    public void Stop()
    {
        _bus.Unsubscribe(OnEventAsync);
        if (_timer != null && _timerHandler != null)
            _timer.Elapsed -= _timerHandler;
        _timerHandler = null;
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }

    private async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(_token))
        {
            PublishMergedViewers();
            return;
        }
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.twitch.tv/helix/chat/chatters?broadcaster_id={_broadcasterId}&moderator_id={_broadcasterId}&first=1000");
            req.Headers.Add("Authorization", $"Bearer {_token}");
            req.Headers.Add("Client-Id", _clientId!);

            using var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var list = new Dictionary<string, ViewerInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var info = new ViewerInfo(
                    item.GetProperty("user_id").GetString()    ?? "",
                    item.GetProperty("user_login").GetString() ?? "",
                    item.GetProperty("user_name").GetString()  ?? "",
                    Platform.Twitch);
                list[ViewerKey(info)] = info;
            }

            lock (_lock) _twitchViewers = list;
            PublishMergedViewers();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[Viewers] Refresh failed: {Message}", ex.Message);
        }
    }

    private Task OnEventAsync(StreamEvent evt)
    {
        if (evt.Platform != Platform.Kick || evt.Type != EventType.Chat) return Task.CompletedTask;

        var info = new ViewerInfo(
            evt.User.Id ?? "",
            evt.User.Username ?? "",
            evt.User.DisplayName ?? evt.User.Username ?? "",
            Platform.Kick);

        lock (_lock)
            _kickViewers[ViewerKey(info)] = (info, DateTimeOffset.UtcNow);

        PublishMergedViewers();
        return Task.CompletedTask;
    }

    private void PruneKickViewers()
    {
        var cutoff = DateTimeOffset.UtcNow - KickViewerTtl;
        lock (_lock)
        {
            var stale = _kickViewers
                .Where(kv => kv.Value.LastSeen < cutoff)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in stale) _kickViewers.Remove(key);
        }
    }

    private void PublishMergedViewers()
    {
        List<ViewerInfo> merged;
        lock (_lock)
        {
            PruneKickViewers_NoLock();
            merged = _twitchViewers.Values
                .Concat(_kickViewers.Values.Select(v => v.Info))
                .OrderBy(v => v.Platform)
                .ThenBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _viewers = merged;
        }
        ViewersUpdated?.Invoke(merged);
    }

    private void PruneKickViewers_NoLock()
    {
        var cutoff = DateTimeOffset.UtcNow - KickViewerTtl;
        var stale = _kickViewers
            .Where(kv => kv.Value.LastSeen < cutoff)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in stale) _kickViewers.Remove(key);
    }

    private static string ViewerKey(ViewerInfo info)
        => !string.IsNullOrWhiteSpace(info.UserId)
            ? $"{info.Platform}:{info.UserId}"
            : $"{info.Platform}:{info.Login}";
}
