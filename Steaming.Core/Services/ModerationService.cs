using System.Text;
using System.Text.Json;
using Steaming.Core.Models;

namespace Steaming.Core.Services;

// Wraps Twitch and Kick moderation endpoints.
// Uses per-request HttpRequestMessage so concurrent calls never race on shared headers.
public class ModerationService
{
    private static readonly HttpClient _http = new();

    private string? _token;
    private string? _clientId;
    private string? _broadcasterId;
    private string? _moderatorId;
    private string? _kickToken;
    private int _kickBroadcasterUserId;

    public bool IsConfigured => !string.IsNullOrEmpty(_token);

    public bool CanModerate(Platform platform)
        => platform switch
        {
            Platform.Twitch => !string.IsNullOrEmpty(_token),
            Platform.Kick   => !string.IsNullOrWhiteSpace(_kickToken) && _kickBroadcasterUserId > 0,
            _               => false,
        };

    public void Configure(string token, string clientId, string broadcasterId)
    {
        _token         = token;
        _clientId      = clientId;
        _broadcasterId = broadcasterId;
        _moderatorId   = broadcasterId;
    }

    public void ConfigureKick(string token, int broadcasterUserId)
    {
        _kickToken             = token;
        _kickBroadcasterUserId = broadcasterUserId;
    }

    public void ClearKick()
    {
        _kickToken             = null;
        _kickBroadcasterUserId = 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private HttpRequestMessage TwitchRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("Authorization", $"Bearer {_token}");
        req.Headers.Add("Client-Id", _clientId!);
        return req;
    }

    private HttpRequestMessage KickRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("Authorization", $"Bearer {_kickToken}");
        return req;
    }

    // ── Timeout ───────────────────────────────────────────────────────────────

    public Task TimeoutAsync(string userId, int durationSeconds, string reason = "")
        => TimeoutAsync(Platform.Twitch, userId, durationSeconds, reason);

    public async Task TimeoutAsync(Platform platform, string userId, int durationSeconds, string reason = "")
    {
        if (platform == Platform.Kick)
        {
            if (!CanModerate(Platform.Kick) || !long.TryParse(userId, out var kickUserId)) return;
            var body = JsonSerializer.Serialize(new
            {
                broadcaster_user_id = _kickBroadcasterUserId,
                user_id  = kickUserId,
                duration = Math.Max(1, (int)Math.Ceiling(durationSeconds / 60d)),
                reason
            });
            var req = KickRequest(HttpMethod.Post, "https://api.kick.com/public/v1/moderation/bans");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            try { await _http.SendAsync(req); } catch { }
            return;
        }

        if (!CanModerate(Platform.Twitch)) return;
        var twitchBody = JsonSerializer.Serialize(new { data = new { user_id = userId, duration = durationSeconds, reason } });
        var treq = TwitchRequest(HttpMethod.Post,
            $"https://api.twitch.tv/helix/moderation/bans?broadcaster_id={_broadcasterId}&moderator_id={_moderatorId}");
        treq.Content = new StringContent(twitchBody, Encoding.UTF8, "application/json");
        try { await _http.SendAsync(treq); } catch { }
    }

    // ── Ban ───────────────────────────────────────────────────────────────────

    public Task BanAsync(string userId, string reason = "")
        => BanAsync(Platform.Twitch, userId, reason);

    public async Task BanAsync(Platform platform, string userId, string reason = "")
    {
        if (platform == Platform.Kick)
        {
            if (!CanModerate(Platform.Kick) || !long.TryParse(userId, out var kickUserId)) return;
            var body = JsonSerializer.Serialize(new
            {
                broadcaster_user_id = _kickBroadcasterUserId,
                user_id = kickUserId,
                reason
            });
            var req = KickRequest(HttpMethod.Post, "https://api.kick.com/public/v1/moderation/bans");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            try { await _http.SendAsync(req); } catch { }
            return;
        }

        if (!CanModerate(Platform.Twitch)) return;
        var twitchBody = JsonSerializer.Serialize(new { data = new { user_id = userId, reason } });
        var treq = TwitchRequest(HttpMethod.Post,
            $"https://api.twitch.tv/helix/moderation/bans?broadcaster_id={_broadcasterId}&moderator_id={_moderatorId}");
        treq.Content = new StringContent(twitchBody, Encoding.UTF8, "application/json");
        try { await _http.SendAsync(treq); } catch { }
    }

    // ── Delete message ────────────────────────────────────────────────────────

    public Task DeleteMessageAsync(string messageId)
        => DeleteMessageAsync(Platform.Twitch, messageId);

    public async Task DeleteMessageAsync(Platform platform, string messageId)
    {
        if (platform == Platform.Kick)
        {
            if (!CanModerate(Platform.Kick) || string.IsNullOrWhiteSpace(messageId)) return;
            var req = KickRequest(HttpMethod.Delete,
                $"https://api.kick.com/public/v1/chat/{Uri.EscapeDataString(messageId)}");
            try { await _http.SendAsync(req); } catch { }
            return;
        }

        if (!CanModerate(Platform.Twitch)) return;
        var treq = TwitchRequest(HttpMethod.Delete,
            $"https://api.twitch.tv/helix/moderation/chat?broadcaster_id={_broadcasterId}&moderator_id={_moderatorId}&message_id={messageId}");
        try { await _http.SendAsync(treq); } catch { }
    }

    // ── Clear chat ────────────────────────────────────────────────────────────

    public async Task ClearChatAsync()
    {
        if (!CanModerate(Platform.Twitch)) return;
        var req = TwitchRequest(HttpMethod.Delete,
            $"https://api.twitch.tv/helix/moderation/chat?broadcaster_id={_broadcasterId}&moderator_id={_moderatorId}");
        try { await _http.SendAsync(req); } catch { }
    }

    // ── User lookup ───────────────────────────────────────────────────────────

    public async Task<string?> GetUserIdAsync(string login)
    {
        if (!CanModerate(Platform.Twitch)) return null;
        var req = TwitchRequest(HttpMethod.Get,
            $"https://api.twitch.tv/helix/users?login={Uri.EscapeDataString(login)}");
        try
        {
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0) return null;
            return data[0].GetProperty("id").GetString();
        }
        catch { return null; }
    }
}
