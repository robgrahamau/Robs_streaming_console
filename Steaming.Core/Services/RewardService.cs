using System.Net.Http;
using System.Text.Json;
using Steaming.Core.Auth;
using Steaming.Core.Models;

namespace Steaming.Core.Services;

// Fetches channel-point rewards from the platform APIs and merges them into AppSettings.Rewards
// non-destructively. Twitch: Helix GET /channel_points/custom_rewards (scope channel:read:redemptions,
// already requested). Kick: public GET /channels/rewards (scope channel:rewards:read — added to the
// Kick login; user must re-authorise Kick once for this to succeed). Never throws to the caller.
public class RewardService
{
    private readonly TokenStore _tokens;
    private readonly AppSettings _settings;

    public RewardService(TokenStore tokens, AppSettings settings)
    {
        _tokens = tokens;
        _settings = settings;
    }

    // Refreshes whichever platforms are logged in, merges (non-destructive), saves, and returns a
    // short human-readable status for the UI.
    public async Task<string> RefreshAsync()
    {
        var parts = new List<string>();

        try
        {
            var tw = await FetchTwitchAsync();
            if (tw != null) { _settings.MergeRewards("Twitch", tw); parts.Add($"Twitch: {tw.Count}"); }
        }
        catch (Exception ex) { parts.Add($"Twitch failed ({ex.Message})"); }

        try
        {
            var kk = await FetchKickAsync();
            if (kk != null) { _settings.MergeRewards("Kick", kk); parts.Add($"Kick: {kk.Count}"); }
        }
        catch (Exception ex) { parts.Add($"Kick failed ({ex.Message})"); }

        _settings.Save();
        return parts.Count > 0 ? string.Join("  ·  ", parts) : "No platform connected.";
    }

    private async Task<List<ChannelReward>?> FetchTwitchAsync()
    {
        var c = _tokens.Credentials;
        if (string.IsNullOrEmpty(c.TwitchAccessToken) ||
            string.IsNullOrEmpty(c.TwitchUserId) ||
            string.IsNullOrEmpty(c.TwitchClientId))
            return null;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {c.TwitchAccessToken}");
        http.DefaultRequestHeaders.Add("Client-Id", c.TwitchClientId);

        var resp = await http.GetAsync(
            $"https://api.twitch.tv/helix/channel_points/custom_rewards?broadcaster_id={Uri.EscapeDataString(c.TwitchUserId)}");
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)resp.StatusCode}");

        return ParseRewards(await resp.Content.ReadAsStringAsync(), "Twitch");
    }

    private async Task<List<ChannelReward>?> FetchKickAsync()
    {
        var c = _tokens.Credentials;
        if (string.IsNullOrEmpty(c.KickAccessToken))
            return null;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {c.KickAccessToken}");
        http.DefaultRequestHeaders.Add("Accept", "application/json");

        var resp = await http.GetAsync("https://api.kick.com/public/v1/channels/rewards");
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)resp.StatusCode}");

        return ParseRewards(await resp.Content.ReadAsStringAsync(), "Kick");
    }

    // Both Twitch Helix and Kick public API return { "data": [ { id, title, cost, is_enabled } ] }.
    private static List<ChannelReward> ParseRewards(string json, string platform)
    {
        var list = new List<ChannelReward>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var r in data.EnumerateArray())
        {
            list.Add(new ChannelReward
            {
                Id       = r.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                Platform = platform,
                Title    = r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                Cost     = r.TryGetProperty("cost", out var co) && co.TryGetInt32(out var ci) ? ci : 0,
                Enabled  = !r.TryGetProperty("is_enabled", out var en) || en.ValueKind != JsonValueKind.False,
            });
        }

        return list;
    }
}
