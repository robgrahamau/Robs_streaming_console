using System.Net.Http;
using System.Text.Json;
using Steaming.Core.Auth;

namespace Steaming.Application.Services;

public sealed class PlatformCredentialService(TokenStore tokens)
{
    public SavedPlatformAuthState GetSavedAuthState()
        => new(
            tokens.Credentials.TwitchChannel ?? "",
            !string.IsNullOrWhiteSpace(tokens.Credentials.TwitchAccessToken),
            tokens.Credentials.TwitchUsername ?? "Connected",
            !string.IsNullOrWhiteSpace(tokens.Credentials.KickAccessToken),
            tokens.Credentials.KickUsername ?? "",
            !string.IsNullOrWhiteSpace(tokens.Credentials.YouTubeAccessToken),
            tokens.Credentials.YouTubeChannelTitle ?? "");

    public void SaveTwitchLogin(string token, string clientId, string username, string channel)
    {
        tokens.Credentials.TwitchAccessToken = token;
        tokens.Credentials.TwitchClientId    = clientId;
        tokens.Credentials.TwitchUsername    = username;
        tokens.Credentials.TwitchChannel     = channel;
        tokens.Save();
    }

    public void ClearTwitchLogin()
    {
        tokens.Credentials.TwitchAccessToken = null;
        tokens.Credentials.TwitchUsername    = null;
        tokens.Credentials.TwitchUserId      = null;
        tokens.Credentials.TwitchChannel     = null;
        tokens.Credentials.TwitchClientId    = null;
        tokens.Save();
    }

    public void SaveKickLogin(string accessToken, string? refreshToken, string clientId, string clientSecret, int chatroomId, string username)
    {
        tokens.Credentials.KickAccessToken  = accessToken;
        tokens.Credentials.KickRefreshToken = refreshToken;
        tokens.Credentials.KickClientId     = clientId;
        tokens.Credentials.KickClientSecret = clientSecret;
        tokens.Credentials.KickChatroomId   = chatroomId;
        tokens.Credentials.KickUsername     = username;
        tokens.Save();
    }

    public void SaveKickIdentity(int broadcasterUserId, string username)
    {
        tokens.Credentials.KickChatroomId = broadcasterUserId;
        tokens.Credentials.KickUsername   = username;
        tokens.Save();
    }

    public void ClearKickLogin()
    {
        tokens.Credentials.KickAccessToken  = null;
        tokens.Credentials.KickRefreshToken = null;
        tokens.Credentials.KickUsername     = null;
        tokens.Credentials.KickClientId     = null;
        tokens.Credentials.KickClientSecret = null;
        tokens.Credentials.KickChatroomId   = 0;
        tokens.Save();
    }

    public void SaveYouTubeLogin(string accessToken, string? refreshToken, DateTimeOffset? expiry,
        string clientId, string clientSecret, string channelId, string channelTitle)
    {
        tokens.Credentials.YouTubeAccessToken  = accessToken;
        tokens.Credentials.YouTubeRefreshToken = refreshToken;
        tokens.Credentials.YouTubeTokenExpiry  = expiry;
        tokens.Credentials.YouTubeClientId     = clientId;
        tokens.Credentials.YouTubeClientSecret = clientSecret;
        tokens.Credentials.YouTubeChannelId    = channelId;
        tokens.Credentials.YouTubeChannelTitle = channelTitle;
        tokens.Save();
    }

    public void ClearYouTubeLogin()
    {
        tokens.Credentials.YouTubeAccessToken  = null;
        tokens.Credentials.YouTubeRefreshToken = null;
        tokens.Credentials.YouTubeTokenExpiry  = null;
        tokens.Credentials.YouTubeClientId     = null;
        tokens.Credentials.YouTubeClientSecret = null;
        tokens.Credentials.YouTubeChannelId    = null;
        tokens.Credentials.YouTubeChannelTitle = null;
        tokens.Save();
    }

    // ── Bot account ───────────────────────────────────────────────────────────

    public BotAuthState GetBotAuthState()
        => new(
            !string.IsNullOrWhiteSpace(tokens.Credentials.BotTwitchAccessToken),
            tokens.Credentials.BotTwitchUsername ?? "",
            !string.IsNullOrWhiteSpace(tokens.Credentials.BotKickAccessToken),
            tokens.Credentials.BotKickUsername ?? "",
            !string.IsNullOrWhiteSpace(tokens.Credentials.BotYouTubeAccessToken),
            tokens.Credentials.BotYouTubeUsername ?? "");

    public void SaveTwitchBotLogin(string token, string username)
    {
        tokens.Credentials.BotTwitchAccessToken = token;
        tokens.Credentials.BotTwitchUsername    = username;
        tokens.Save();
    }

    public void ClearTwitchBotLogin()
    {
        tokens.Credentials.BotTwitchAccessToken = null;
        tokens.Credentials.BotTwitchUsername    = null;
        tokens.Save();
    }

    public void SaveKickBotLogin(string accessToken, string? refreshToken, string username)
    {
        tokens.Credentials.BotKickAccessToken  = accessToken;
        tokens.Credentials.BotKickRefreshToken = refreshToken;
        tokens.Credentials.BotKickUsername     = username;
        tokens.Save();
    }

    public void ClearKickBotLogin()
    {
        tokens.Credentials.BotKickAccessToken  = null;
        tokens.Credentials.BotKickRefreshToken = null;
        tokens.Credentials.BotKickUsername     = null;
        tokens.Save();
    }

    public void SaveYouTubeBotLogin(string accessToken, string? refreshToken, DateTimeOffset? expiry, string channelId, string username)
    {
        tokens.Credentials.BotYouTubeAccessToken  = accessToken;
        tokens.Credentials.BotYouTubeRefreshToken = refreshToken;
        tokens.Credentials.BotYouTubeTokenExpiry  = expiry;
        tokens.Credentials.BotYouTubeChannelId    = channelId;
        tokens.Credentials.BotYouTubeUsername     = username;
        tokens.Save();
    }

    public void ClearYouTubeBotLogin()
    {
        tokens.Credentials.BotYouTubeAccessToken  = null;
        tokens.Credentials.BotYouTubeRefreshToken = null;
        tokens.Credentials.BotYouTubeTokenExpiry  = null;
        tokens.Credentials.BotYouTubeChannelId    = null;
        tokens.Credentials.BotYouTubeUsername     = null;
        tokens.Save();
    }

    public async Task<string> FetchTwitchUsernameAsync(string token, string clientId)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            http.DefaultRequestHeaders.Add("Client-Id", clientId);
            var json = await http.GetStringAsync("https://api.twitch.tv/helix/users");
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("data")[0].GetProperty("login").GetString() ?? "unknown";
        }
        catch { return "unknown"; }
    }

    public async Task<(int chatroomId, string username, string diagnostics)> FetchKickUserInfoAsync(string token)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            var usersJson = await http.GetStringAsync("https://api.kick.com/public/v1/users");
            using var doc = JsonDocument.Parse(usersJson);
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0) return (0, "", "Users API returned empty data array");

            var user = data[0];
            if (!user.TryGetProperty("user_id", out var uid) || uid.GetInt32() <= 0)
                return (0, "", "Users API did not return a usable user_id");

            var broadcasterUserId = uid.GetInt32();
            var chanJson = await http.GetStringAsync(
                $"https://api.kick.com/public/v1/channels?broadcaster_user_id={broadcasterUserId}");
            using var chanDoc  = JsonDocument.Parse(chanJson);
            var chanData = chanDoc.RootElement.GetProperty("data");
            if (chanData.GetArrayLength() > 0)
            {
                var chan = chanData[0];
                if (chan.TryGetProperty("slug", out var slugProp))
                {
                    var slug = slugProp.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(slug))
                        return (broadcasterUserId, slug, "");
                }
            }

            var fallbackName = user.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
            return (broadcasterUserId, fallbackName, "Channels API did not return a slug; using user name fallback");
        }
        catch (Exception ex)
        {
            return (0, "", $"Exception: {ex.Message}");
        }
    }

    public async Task<KickTokenExchangeResult?> RefreshKickTokenAsync()
    {
        var refreshToken = tokens.Credentials.KickRefreshToken;
        var clientId     = tokens.Credentials.KickClientId;
        var clientSecret = tokens.Credentials.KickClientSecret;
        if (string.IsNullOrWhiteSpace(refreshToken) || string.IsNullOrWhiteSpace(clientId)) return null;
        try
        {
            using var http = new HttpClient();
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "refresh_token",
                ["client_id"]     = clientId,
                ["client_secret"] = clientSecret ?? "",
                ["refresh_token"] = refreshToken,
            });
            var resp = await http.PostAsync("https://id.kick.com/oauth/token", body);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            using var doc  = JsonDocument.Parse(json);
            var root       = doc.RootElement;
            var accessToken = root.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(accessToken)) return null;
            var newRefresh = root.TryGetProperty("refresh_token", out var rp) ? rp.GetString() : refreshToken;
            return new KickTokenExchangeResult(accessToken, newRefresh);
        }
        catch { return null; }
    }

    public async Task<KickTokenExchangeResult?> ExchangeKickCodeAsync(
        string code, string codeVerifier, string clientId, string clientSecret, string redirectUri)
    {
        try
        {
            using var http = new HttpClient();
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "authorization_code",
                ["client_id"]     = clientId,
                ["client_secret"] = clientSecret,
                ["code"]          = code,
                ["redirect_uri"]  = redirectUri,
                ["code_verifier"] = codeVerifier,
            });
            var resp        = await http.PostAsync("https://id.kick.com/oauth/token", body);
            var json        = await resp.Content.ReadAsStringAsync();
            using var doc   = JsonDocument.Parse(json);
            var root        = doc.RootElement;
            var accessToken = root.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(accessToken)) return null;
            var refreshToken = root.TryGetProperty("refresh_token", out var rp) ? rp.GetString() : null;
            return new KickTokenExchangeResult(accessToken, refreshToken);
        }
        catch { return null; }
    }

    public async Task<YouTubeTokenExchangeResult?> ExchangeYouTubeCodeAsync(
        string code, string codeVerifier, string clientId, string clientSecret, string redirectUri)
    {
        try
        {
            using var http = new HttpClient();
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "authorization_code",
                ["client_id"]     = clientId,
                ["client_secret"] = clientSecret,
                ["code"]          = code,
                ["redirect_uri"]  = redirectUri,
                ["code_verifier"] = codeVerifier,
            });
            var resp = await http.PostAsync("https://oauth2.googleapis.com/token", body);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return ParseYouTubeTokenExchange(doc.RootElement);
        }
        catch { return null; }
    }

    public async Task<YouTubeTokenExchangeResult?> RefreshYouTubeTokenAsync(bool bot = false)
    {
        var refreshToken = bot ? tokens.Credentials.BotYouTubeRefreshToken : tokens.Credentials.YouTubeRefreshToken;
        var clientId = bot ? (tokens.Credentials.YouTubeClientId ?? "") : (tokens.Credentials.YouTubeClientId ?? "");
        var clientSecret = bot ? (tokens.Credentials.YouTubeClientSecret ?? "") : (tokens.Credentials.YouTubeClientSecret ?? "");
        if (string.IsNullOrWhiteSpace(refreshToken) || string.IsNullOrWhiteSpace(clientId)) return null;
        try
        {
            using var http = new HttpClient();
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "refresh_token",
                ["client_id"]     = clientId,
                ["client_secret"] = clientSecret,
                ["refresh_token"] = refreshToken,
            });
            var resp = await http.PostAsync("https://oauth2.googleapis.com/token", body);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return ParseYouTubeTokenExchange(doc.RootElement);
        }
        catch { return null; }
    }

    public async Task<(string channelId, string title)> FetchYouTubeChannelInfoAsync(string token)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            var json = await http.GetStringAsync("https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true");
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("items");
            if (data.GetArrayLength() == 0) return ("", "");
            var item = data[0];
            var channelId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var title = item.TryGetProperty("snippet", out var snippet) &&
                        snippet.TryGetProperty("title", out var titleEl)
                ? titleEl.GetString() ?? ""
                : "";
            return (channelId, title);
        }
        catch
        {
            return ("", "");
        }
    }

    private static YouTubeTokenExchangeResult? ParseYouTubeTokenExchange(JsonElement root)
    {
        var accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        if (string.IsNullOrWhiteSpace(accessToken)) return null;
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        DateTimeOffset? expiry = null;
        if (root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var seconds) && seconds > 0)
            expiry = DateTimeOffset.UtcNow.AddSeconds(seconds);
        return new YouTubeTokenExchangeResult(accessToken, refreshToken, expiry);
    }
}

public sealed record SavedPlatformAuthState(
    string TwitchChannel,
    bool   HasTwitchToken,
    string TwitchUsername,
    bool   HasKickToken,
    string KickUsername,
    bool   HasYouTubeToken,
    string YouTubeChannelTitle);

public sealed record BotAuthState(
    bool   HasTwitchBot,
    string TwitchBotUsername,
    bool   HasKickBot,
    string KickBotUsername,
    bool   HasYouTubeBot,
    string YouTubeBotUsername);

public sealed record KickTokenExchangeResult(string AccessToken, string? RefreshToken);
public sealed record YouTubeTokenExchangeResult(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiryUtc);
