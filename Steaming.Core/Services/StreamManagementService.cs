using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Steaming.Core.Auth;
using Steaming.Core.Models;

namespace Steaming.Core.Services;

public record ChannelInfo(string Title, string GameId, string GameName);
public record GameSearchResult(string Id, string Name, string? BoxArtUrl, Platform Platform);
public record ChannelMetadataSnapshot(ChannelInfo? Twitch, ChannelInfo? Kick);
public record PlatformUpdateResult(
    bool TwitchRequested,
    bool TwitchSucceeded,
    bool KickRequested,
    bool KickSucceeded,
    string? TwitchError = null,
    string? KickError = null)
{
    public bool AnyRequested => TwitchRequested || KickRequested;
    public bool AllSucceeded => (!TwitchRequested || TwitchSucceeded) && (!KickRequested || KickSucceeded);
}

// Wraps Twitch Helix channel management endpoints.
[SupportedOSPlatform("windows")]
public class StreamManagementService
{
    private readonly HttpClient _http = new();
    private readonly TokenStore _tokens;
    private readonly ILogger<StreamManagementService> _logger;
    private string? _token;
    private string? _clientId;
    private string? _broadcasterId;

    public bool IsConfigured => IsTwitchConfigured || IsKickConfigured;
    public bool IsTwitchConfigured => !string.IsNullOrEmpty(_token);
    public bool IsKickConfigured =>
        !string.IsNullOrWhiteSpace(_tokens.Credentials.KickAccessToken) &&
        _tokens.Credentials.KickChatroomId > 0;

    public StreamManagementService(TokenStore tokens, ILogger<StreamManagementService> logger)
    {
        _tokens = tokens;
        _logger = logger;
    }

    public void Configure(string token, string clientId, string broadcasterId)
    {
        _token         = token;
        _clientId      = clientId;
        _broadcasterId = broadcasterId;
    }

    private void ApplyHeaders()
    {
        _http.DefaultRequestHeaders.Clear();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_token}");
        _http.DefaultRequestHeaders.Add("Client-Id", _clientId!);
    }

    private void ApplyKickHeaders()
    {
        _http.DefaultRequestHeaders.Clear();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_tokens.Credentials.KickAccessToken}");
    }

    public async Task<ChannelInfo?> GetTwitchChannelInfoAsync()
    {
        if (!IsTwitchConfigured) return null;
        ApplyHeaders();
        try
        {
            var json = await _http.GetStringAsync(
                $"https://api.twitch.tv/helix/channels?broadcaster_id={_broadcasterId}");
            using var doc = JsonDocument.Parse(json);
            var d = doc.RootElement.GetProperty("data")[0];
            return new ChannelInfo(
                d.GetProperty("title").GetString()     ?? "",
                d.GetProperty("game_id").GetString()   ?? "",
                d.GetProperty("game_name").GetString() ?? "");
        }
        catch (Exception ex)
        {
            LogDebug($"[Stream] Twitch metadata fetch failed: {ex.Message}");
            _logger.LogDebug("[Stream] Twitch metadata fetch failed: {Msg}", ex.Message);
            return null;
        }
    }

    public async Task<ChannelInfo?> GetKickChannelInfoAsync()
    {
        if (!IsKickConfigured)
        {
            LogDebug("[Stream] Kick metadata fetch skipped: Kick token or broadcaster id is missing.");
            return KickMetadataCache.Get();
        }
        ApplyKickHeaders();
        try
        {
            var directJson = await _http.GetStringAsync("https://api.kick.com/public/v1/channels");
            var directInfo = TryParseKickChannelInfo(directJson, "self");
            if (directInfo != null) return directInfo;

            LogDebug("[Stream] Kick metadata self lookup returned no usable channel info. Falling back to broadcaster_user_id query.");

            var queryJson = await _http.GetStringAsync(
                $"https://api.kick.com/public/v1/channels?broadcaster_user_id={_tokens.Credentials.KickChatroomId}");
            var queryInfo = TryParseKickChannelInfo(queryJson, $"broadcaster_user_id={_tokens.Credentials.KickChatroomId}");
            if (queryInfo != null) return queryInfo;

            LogDebug("[Stream] Kick metadata fetch returned no usable channel info from either endpoint.");
            var cached = KickMetadataCache.Get();
            if (cached != null)
                LogDebug($"[Stream] Using cached Kick metadata fallback. title='{cached.Title}' category='{cached.GameName}'");
            return cached;
        }
        catch (Exception ex)
        {
            LogDebug($"[Stream] Kick metadata fetch failed: {ex.Message}");
            _logger.LogDebug("[Stream] Kick metadata fetch failed: {Msg}", ex.Message);
            return KickMetadataCache.Get();
        }
    }

    public async Task<PlatformUpdateResult> UpdateTitleAsync(string title, bool updateTwitch, bool updateKick)
    {
        var twitchOk = !updateTwitch;
        var kickOk = !updateKick;
        string? twitchError = null;
        string? kickError = null;

        if (updateTwitch && IsTwitchConfigured)
        {
            ApplyHeaders();
            var body = JsonSerializer.Serialize(new { title });
            var resp = await _http.PatchAsync(
                $"https://api.twitch.tv/helix/channels?broadcaster_id={_broadcasterId}",
                new StringContent(body, Encoding.UTF8, "application/json"));
            twitchOk = resp.IsSuccessStatusCode;
            if (!twitchOk)
                twitchError = await BuildHttpErrorAsync("Twitch title update", resp);
        }

        if (updateKick && IsKickConfigured)
        {
            ApplyKickHeaders();
            var body = JsonSerializer.Serialize(new { stream_title = title });
            var resp = await _http.PatchAsync(
                "https://api.kick.com/public/v1/channels",
                new StringContent(body, Encoding.UTF8, "application/json"));
            kickOk = resp.IsSuccessStatusCode;
            if (!kickOk)
                kickError = await BuildHttpErrorAsync("Kick title update", resp);
            else
                KickMetadataCache.Merge(title: title);
        }
        else if (updateKick)
        {
            kickOk = false;
            kickError = "Kick metadata is not configured. Re-login is required.";
            LogDebug("[Stream] Kick title update skipped: Kick token or broadcaster id is missing.");
        }

        return new PlatformUpdateResult(updateTwitch, twitchOk, updateKick, kickOk, twitchError, kickError);
    }

    public async Task<List<GameSearchResult>> SearchGamesAsync(string query, bool searchTwitch, bool searchKick)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var results = new List<GameSearchResult>();

        if (searchTwitch && IsTwitchConfigured)
        {
            ApplyHeaders();
            try
            {
                var json = await _http.GetStringAsync(
                    $"https://api.twitch.tv/helix/search/categories?query={Uri.EscapeDataString(query)}&first=10");
                using var doc = JsonDocument.Parse(json);
                foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
                    results.Add(new GameSearchResult(
                        item.GetProperty("id").GetString()           ?? "",
                        item.GetProperty("name").GetString()         ?? "",
                        item.TryGetProperty("box_art_url", out var u) ? u.GetString() : null,
                        Platform.Twitch));
            }
            catch (Exception ex)
            {
                LogDebug($"[Stream] Twitch category search failed: {ex.Message}");
                _logger.LogDebug("[Stream] Twitch category search failed: {Msg}", ex.Message);
            }
        }

        if (searchKick && IsKickConfigured)
        {
            ApplyKickHeaders();
            try
            {
                var json = await _http.GetStringAsync(
                    $"https://api.kick.com/public/v1/categories?q={Uri.EscapeDataString(query)}&page=1");
                using var doc = JsonDocument.Parse(json);
                foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
                    results.Add(new GameSearchResult(
                        item.GetProperty("id").GetInt32().ToString(),
                        item.GetProperty("name").GetString() ?? "",
                        item.TryGetProperty("thumbnail", out var u) ? u.GetString() : null,
                        Platform.Kick));
            }
            catch (Exception ex)
            {
                LogDebug($"[Stream] Kick category search failed: {ex.Message}");
                _logger.LogDebug("[Stream] Kick category search failed: {Msg}", ex.Message);
            }
        }
        else if (searchKick)
        {
            LogDebug("[Stream] Kick category search skipped: Kick token or broadcaster id is missing.");
        }

        return results;
    }

    public async Task<PlatformUpdateResult> UpdateGameAsync(GameSearchResult selected, bool updateTwitch, bool updateKick)
    {
        var twitchOk = !updateTwitch;
        var kickOk = !updateKick;
        string? twitchError = null;
        string? kickError = null;

        if (updateTwitch && IsTwitchConfigured)
        {
            var twitchSelection = selected.Platform == Platform.Twitch
                ? selected
                : await FindBestCategoryAsync(selected.Name, Platform.Twitch);

            if (twitchSelection != null)
            {
                ApplyHeaders();
                var body = JsonSerializer.Serialize(new { game_id = twitchSelection.Id });
                var resp = await _http.PatchAsync(
                    $"https://api.twitch.tv/helix/channels?broadcaster_id={_broadcasterId}",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                twitchOk = resp.IsSuccessStatusCode;
                if (!twitchOk)
                    twitchError = await BuildHttpErrorAsync("Twitch category update", resp);
            }
            else
            {
                twitchOk = false;
                twitchError = "No matching Twitch category found.";
            }
        }

        if (updateKick && IsKickConfigured)
        {
            var kickSelection = selected.Platform == Platform.Kick
                ? selected
                : await FindBestCategoryAsync(selected.Name, Platform.Kick);

            if (kickSelection != null)
            {
                ApplyKickHeaders();
                var body = JsonSerializer.Serialize(new { category_id = int.Parse(kickSelection.Id) });
                var resp = await _http.PatchAsync(
                    "https://api.kick.com/public/v1/channels",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                kickOk = resp.IsSuccessStatusCode;
                if (!kickOk)
                    kickError = await BuildHttpErrorAsync("Kick category update", resp);
                else
                    KickMetadataCache.Merge(gameId: kickSelection.Id, gameName: kickSelection.Name);
            }
            else
            {
                kickOk = false;
                kickError = "No matching Kick category found.";
            }
        }
        else if (updateKick)
        {
            kickOk = false;
            kickError = "Kick metadata is not configured. Re-login is required.";
            LogDebug("[Stream] Kick category update skipped: Kick token or broadcaster id is missing.");
        }

        return new PlatformUpdateResult(updateTwitch, twitchOk, updateKick, kickOk, twitchError, kickError);
    }

    private async Task<GameSearchResult?> FindBestCategoryAsync(string name, Platform platform)
    {
        var results = await SearchGamesAsync(name, platform == Platform.Twitch, platform == Platform.Kick);
        return results
            .Where(r => r.Platform == platform)
            .OrderByDescending(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
            .ThenBy(r => r.Name.Length)
            .FirstOrDefault();
    }

    private ChannelInfo? TryParseKickChannelInfo(string json, string sourceLabel)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                LogDebug($"[Stream] Kick metadata parse ({sourceLabel}) missing data array. Body={Truncate(json)}");
                return null;
            }

            if (data.GetArrayLength() == 0)
            {
                LogDebug($"[Stream] Kick metadata parse ({sourceLabel}) returned empty data array. Body={Truncate(json)}");
                return null;
            }

            var d = data[0];
            var title = d.TryGetProperty("stream_title", out var titleProp) ? titleProp.GetString() ?? "" : "";
            var gameId = "";
            var gameName = "";
            if (d.TryGetProperty("category", out var cat))
            {
                if (cat.TryGetProperty("id", out var idProp))
                    gameId = idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt32().ToString() : idProp.GetString() ?? "";
                if (cat.TryGetProperty("name", out var nameProp))
                    gameName = nameProp.GetString() ?? "";
            }

            LogDebug($"[Stream] Kick metadata parse ({sourceLabel}) title='{title}' category='{gameName}' raw={Truncate(json)}");

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(gameName))
                return null;

            var info = new ChannelInfo(title, gameId, gameName);
            KickMetadataCache.Set(info);
            return info;
        }
        catch (Exception ex)
        {
            LogDebug($"[Stream] Kick metadata parse ({sourceLabel}) failed: {ex.Message} Body={Truncate(json)}");
            return null;
        }
    }

    private async Task<string> BuildHttpErrorAsync(string op, HttpResponseMessage response)
    {
        string body;
        try { body = await response.Content.ReadAsStringAsync(); }
        catch (Exception ex) { body = $"<body read failed: {ex.Message}>"; }

        var msg = $"[Stream] {op} failed: {(int)response.StatusCode} {response.StatusCode} {body}";
        LogDebug(msg);
        _logger.LogDebug(msg);
        return $"{(int)response.StatusCode} {response.StatusCode}: {body}";
    }

    private void LogDebug(string message)
    {
        try { DebugLogFile.Append(message); } catch { }
    }

    private static string Truncate(string text, int max = 400)
        => text.Length <= max ? text : text[..max] + "...";
}
