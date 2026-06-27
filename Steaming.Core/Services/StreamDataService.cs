using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Steaming.Core.Auth;
using Steaming.Core.Models;

namespace Steaming.Core.Services;

// Polls Twitch Helix every 30 seconds for live stream data (viewer count,
// follower count, subscriber count, stream uptime) and fires events through
// the EventBus so OverlayDispatcher can send label/goal updates to OBS.
[SupportedOSPlatform("windows")]
public class StreamDataService : IAsyncDisposable
{
    // Shared clients — HttpClient is thread-safe and must not be created per-call.
    private static readonly HttpClient _twitchHttp = new();
    private static readonly HttpClient _kickHttp   = new();
    private static readonly HttpClient _youtubeHttp = new();

    private readonly EventBus _bus;
    private readonly AppSettings _settings;
    private readonly TokenStore _tokens;
    private readonly ILogger<StreamDataService> _logger;
    // YouTube live state + concurrent viewers are owned by the chat service (it resolves the broadcast);
    // we read its ActiveBroadcastId and poll concurrent viewers here so all three platforms share one
    // viewer/live pipeline into the dashboard.
    private readonly Platforms.YouTubeLiveChatService _youtube;
    // Access token that 401'd with a failed refresh. Polling is blocked only while the stored
    // token still equals this value — when another flow (bridge bootstrap, re-login) saves a new
    // token, polling resumes automatically on the next tick. A permanent bool latch here killed
    // Kick stats for the whole session after a startup refresh race.
    private string? _kickAuthFailedToken;
    private bool _twitchApiOk = true;
    private int  _twitchEmptyPollCount = 0;
    private string _kickStreamTitle    = "";
    private string _kickStreamCategory = "";

    // Newest Twitch follower as last reported by the helix poll. Used to detect actual NEW
    // Twitch follows — RecentFollower itself may legitimately hold a more recent Kick follow.
    private string? _lastTwitchNewestFollower;

    private string? _oauthToken;
    private string? _clientId;
    private string? _broadcasterId;
    private string? _channelLogin;

    public event Action<bool, string, string>? KickAuthStatusChanged;
    public event Action<bool, string, string>? TwitchAuthStatusChanged;
    // Fires after this service refreshes and stores a NEW Kick token. The remote
    // bridge still holds the old one and must be re-bootstrapped or its outbound
    // chat and webhook subscriptions start failing with 401.
    public event Action? KickTokenRefreshed;

    private CancellationTokenSource _cts = new();
    private Task _pollTask = Task.CompletedTask;
    private bool _started;

    private int _twitchViewerCount;
    private int _kickViewerCount;
    private int _youtubeViewerCount;
    private int _twitchFollowerCount;
    private int _kickFollowerCount;
    private int _twitchSubscriberCount;
    private int _kickSubscriberCount;
    // Kick follower totals are sourced from the opt-in unsupported browser/Pusher path when enabled.
    // The official public API still does not expose this count in the channels response.
    private bool _kickFollowerCountKnown;

    // Cached live values
    public int    ViewerCount      { get; private set; }
    public int    FollowerCount    { get; private set; }
    public int    SubscriberCount  { get; private set; }
    public int    TwitchViewerCount => _twitchViewerCount;
    public int    KickViewerCount   => _kickViewerCount;
    public int    YouTubeViewerCount => _youtubeViewerCount;
    public int    TwitchFollowerCount   => _twitchFollowerCount;
    public int    KickFollowerCount     => _kickFollowerCount;
    public bool   KickFollowerCountKnown => _kickFollowerCountKnown;
    public int    TwitchSubscriberCount => _twitchSubscriberCount;
    public int    KickSubscriberCount   => _kickSubscriberCount;
    public string StreamTitle      { get; private set; } = "";
    public string StreamCategory   { get; private set; } = "";
    public string KickStreamTitle    => _kickStreamTitle;
    public string KickStreamCategory => _kickStreamCategory;
    public DateTimeOffset? StreamStartedAt { get; private set; }
    public bool   TwitchIsLive   { get; private set; }
    public bool   KickIsLive     { get; private set; }
    public bool   YouTubeIsLive  { get; private set; }
    public bool   IsLive         => TwitchIsLive || KickIsLive || YouTubeIsLive;

    // Recent event tracking — volatile so cross-thread reads always see the latest write.
    private volatile string _recentFollower   = "—";
    private volatile string _recentSubscriber = "—";
    private volatile string _recentGiftSub    = "—";
    private volatile string _recentDonation   = "—";
    private volatile string _topDonation      = "—";
    private double _donationTotal;

    public string RecentFollower   { get => _recentFollower;   set => _recentFollower   = value; }
    public string RecentSubscriber { get => _recentSubscriber; set => _recentSubscriber = value; }
    public string RecentGiftSub    { get => _recentGiftSub;    set => _recentGiftSub    = value; }
    public string RecentDonation   { get => _recentDonation;   set => _recentDonation   = value; }
    public string TopDonation      { get => _topDonation;      set => _topDonation      = value; }
    public double DonationTotal    { get => _donationTotal;    set => _donationTotal    = value; }

    public StreamDataService(EventBus bus, AppSettings settings, TokenStore tokens, ILogger<StreamDataService> logger,
        Platforms.YouTubeLiveChatService youtube)
    {
        _bus      = bus;
        _settings = settings;
        _tokens   = tokens;
        _logger   = logger;
        _youtube  = youtube;
        _kickFollowerCount = _settings.KickRaidAlertsEnabled ? Math.Max(0, _settings.KickFollowerCountEstimate) : 0;
        _kickFollowerCountKnown = _settings.KickRaidAlertsEnabled && _settings.KickFollowerCountEstimate > 0;
    }

    public void Start()
    {
        _kickAuthFailedToken = null;
        _kickFollowerCount = _settings.KickRaidAlertsEnabled ? Math.Max(0, _settings.KickFollowerCountEstimate) : 0;
        _kickFollowerCountKnown = _settings.KickRaidAlertsEnabled && _settings.KickFollowerCountEstimate > 0;
        // Restore the last follower (either platform) persisted by a previous run
        if (_recentFollower == "—" && !string.IsNullOrWhiteSpace(_settings.LastFollowerName))
            _recentFollower = _settings.LastFollowerName;
        RecalculateTotals();
        if (_started) return;
        _started  = true;
        _pollTask = PollLoopAsync(_cts.Token);
    }

    public void Start(string oauthToken, string clientId, string broadcasterId, string channelLogin)
    {
        _oauthToken    = oauthToken;
        _clientId      = clientId;
        _broadcasterId = broadcasterId;
        _channelLogin  = channelLogin;
        Start();
    }

    public void Stop()
    {
        _cts.Cancel();
        _started = false;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // First poll immediately, then every 30 seconds
        while (!ct.IsCancellationRequested)
        {
            try { await PollOnceAsync(); } catch (Exception ex)
            { _logger.LogDebug("[StreamData] Poll error: {Msg}", ex.Message); }
            try { await Task.Delay(30_000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private static HttpRequestMessage TwitchReq(string url, string oauthToken, string clientId)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Authorization", $"Bearer {oauthToken}");
        req.Headers.Add("Client-Id", clientId);
        return req;
    }

    private async Task PollOnceAsync()
    {
        bool changed = false;
        var twitchToken         = _tokens.Credentials.TwitchAccessToken;
        var twitchClientId      = _tokens.Credentials.TwitchClientId;
        var twitchBroadcasterId = _tokens.Credentials.TwitchUserId;

        if (_settings.TwitchActive &&
            !string.IsNullOrEmpty(twitchToken) &&
            !string.IsNullOrEmpty(twitchClientId) &&
            !string.IsNullOrEmpty(twitchBroadcasterId))
        {
            // Viewer count + uptime from /streams
            try
            {
                using var resp = await _twitchHttp.SendAsync(TwitchReq(
                    $"https://api.twitch.tv/helix/streams?user_id={twitchBroadcasterId}",
                    twitchToken, twitchClientId));

                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (_twitchApiOk)
                    {
                        _twitchApiOk = false;
                        TwitchAuthStatusChanged?.Invoke(false, "Twitch login expired",
                            "Twitch API returned 401 — viewer counts, live detection, and stream titles will not update. Re-connect Twitch to fix this.");
                        _logger.LogWarning("[StreamData] Twitch poll 401 — token expired.");
                    }
                    _twitchEmptyPollCount++;
                }
                else if (!resp.IsSuccessStatusCode)
                {
                    // Non-401 failure (429, 500, 503, etc.) — log and treat as empty poll.
                    _twitchEmptyPollCount++;
                    _logger.LogWarning("[StreamData] Twitch streams poll HTTP {Status} — treating as empty.", (int)resp.StatusCode);
                }
                else
                {
                    if (!_twitchApiOk)
                    {
                        _twitchApiOk = true;
                        TwitchAuthStatusChanged?.Invoke(true, "Twitch API restored", "Twitch polling recovered.");
                    }
                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var data = doc.RootElement.GetProperty("data");
                    if (data.GetArrayLength() > 0)
                    {
                        _twitchEmptyPollCount = 0;
                        var stream = data[0];
                        int vc = stream.TryGetProperty("viewer_count", out var vcp) ? vcp.GetInt32() : 0;
                        if (vc != _twitchViewerCount) { _twitchViewerCount = vc; changed = true; }

                        if (stream.TryGetProperty("started_at", out var sat))
                        {
                            if (DateTimeOffset.TryParse(sat.GetString(), out var dt))
                                StreamStartedAt = dt;
                        }

                        string twitchTitle = stream.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
                        string twitchCategory = stream.TryGetProperty("game_name", out var gameEl) ? gameEl.GetString() ?? "" : "";
                        StreamTitle    = twitchTitle;
                        StreamCategory = twitchCategory;

                        if (!TwitchIsLive) { TwitchIsLive = true; changed = true; }
                    }
                    else
                    {
                        _twitchEmptyPollCount++;
                        if (_twitchEmptyPollCount >= 2)
                        {
                            if (_twitchViewerCount != 0) { _twitchViewerCount = 0; changed = true; }
                            StreamStartedAt = null;
                            if (TwitchIsLive) { TwitchIsLive = false; changed = true; }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _twitchEmptyPollCount++;
                _logger.LogDebug("[StreamData] Streams poll: {Msg}", ex.Message);
            }

            // Follower count + most recent follower
            try
            {
                using var resp = await _twitchHttp.SendAsync(TwitchReq(
                    $"https://api.twitch.tv/helix/channels/followers?broadcaster_id={twitchBroadcasterId}&moderator_id={twitchBroadcasterId}&first=1",
                    twitchToken, twitchClientId));
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("total", out var tot))
                {
                    int fc = tot.GetInt32();
                    if (fc != _twitchFollowerCount) { _twitchFollowerCount = fc; changed = true; }
                }
                if (doc.RootElement.TryGetProperty("data", out var arr) && arr.GetArrayLength() > 0)
                {
                    var newest = arr[0];
                    string name = newest.TryGetProperty("user_name", out var un) ? un.GetString() ?? "—" : "—";
                    DateTimeOffset? followedAt =
                        newest.TryGetProperty("followed_at", out var fa)
                        && DateTimeOffset.TryParse(fa.GetString(), out var faDt) ? faDt : null;

                    // Only a CHANGE in Twitch's newest follower is a new follow. Overwriting
                    // whenever it merely differed stomped Kick follows within 30 seconds.
                    if (name != "—" && name != _lastTwitchNewestFollower)
                    {
                        bool firstSeed = _lastTwitchNewestFollower == null;
                        _lastTwitchNewestFollower = name;

                        bool adopt;
                        if (!firstSeed)
                            adopt = true;
                        else if (RecentFollower == "—")
                            adopt = true;
                        else
                            adopt = followedAt.HasValue
                                 && _settings.LastFollowerAt is DateTimeOffset lastAt
                                 && followedAt.Value > lastAt;

                        if (adopt)
                        {
                            if (RecentFollower != name) { RecentFollower = name; changed = true; }
                            PersistLastFollower(name, followedAt ?? DateTimeOffset.UtcNow);
                        }
                    }
                }
            }
            catch (Exception ex) { _logger.LogDebug("[StreamData] Followers poll: {Msg}", ex.Message); }

            // Subscriber count (requires channel:read:subscriptions scope)
            try
            {
                using var resp = await _twitchHttp.SendAsync(TwitchReq(
                    $"https://api.twitch.tv/helix/subscriptions?broadcaster_id={twitchBroadcasterId}",
                    twitchToken, twitchClientId));
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("total", out var tot))
                {
                    int sc = tot.GetInt32();
                    if (sc != _twitchSubscriberCount) { _twitchSubscriberCount = sc; changed = true; }
                }
            }
            catch (Exception ex) { _logger.LogDebug("[StreamData] Subs poll: {Msg}", ex.Message); }
        }
        else
        {
            _twitchEmptyPollCount = 0;
            _lastTwitchNewestFollower = null;
            if (_twitchViewerCount != 0) { _twitchViewerCount = 0; changed = true; }
            if (_twitchFollowerCount != 0) { _twitchFollowerCount = 0; changed = true; }
            if (_twitchSubscriberCount != 0) { _twitchSubscriberCount = 0; changed = true; }
            if (TwitchIsLive) { TwitchIsLive = false; changed = true; }
            if (StreamStartedAt != null) StreamStartedAt = null;
            if (!_twitchApiOk) _twitchApiOk = true;
        }

        changed |= await PollKickCountsAsync();
        changed |= await PollYouTubeViewersAsync();

        // Merge title/category: Twitch is authoritative when live; Kick is fallback.
        // This handles Kick-only streams AND cases where the Twitch poll 401'd.
        if (!TwitchIsLive)
        {
            var fallbackTitle    = !string.IsNullOrEmpty(_kickStreamTitle)    ? _kickStreamTitle    : "";
            var fallbackCategory = !string.IsNullOrEmpty(_kickStreamCategory) ? _kickStreamCategory : "";
            if (KickIsLive)
            {
                StreamTitle    = fallbackTitle;
                StreamCategory = fallbackCategory;
            }
            else
            {
                StreamTitle    = "";
                StreamCategory = "";
            }
        }

        if (changed)
        {
            RecalculateTotals();
            await PublishUpdatedAsync();
        }
    }

    private async Task<bool> PollKickCountsAsync()
    {
        var kickToken = _tokens.Credentials.KickAccessToken;
        var kickBroadcasterId = _tokens.Credentials.KickChatroomId;
        if (!_settings.KickActive || string.IsNullOrWhiteSpace(kickToken) || kickBroadcasterId <= 0)
        {
            var changed = _kickViewerCount != 0 || _kickSubscriberCount != 0;
            _kickViewerCount = 0;
            _kickSubscriberCount = 0;
            return changed;
        }

        // Don't hammer the API with a token that already 401'd and could not be refreshed.
        // A new token stored by any other flow clears this automatically.
        if (kickToken == _kickAuthFailedToken)
            return false;

        HttpRequestMessage KickReq() {
            var r = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.kick.com/public/v1/channels?broadcaster_user_id={kickBroadcasterId}");
            r.Headers.Add("Authorization", $"Bearer {kickToken}");
            return r;
        }

        try
        {
            var response = await _kickHttp.SendAsync(KickReq());
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (!await TryRefreshKickTokenAsync())
                {
                    _kickAuthFailedToken = kickToken;
                    KickAuthStatusChanged?.Invoke(false, "Kick login expired", "Kick API token refresh failed. Polling resumes automatically when a new Kick token is stored (bridge bootstrap or re-login).");
                    _logger.LogWarning("[StreamData] Kick channel poll paused after 401 because token refresh failed; will resume when a new token is stored.");
                    return false;
                }

                kickToken = _tokens.Credentials.KickAccessToken;
                if (string.IsNullOrWhiteSpace(kickToken))
                {
                    KickAuthStatusChanged?.Invoke(false, "Kick login expired", "Kick API token refresh produced no access token. Reconnect Kick to resume viewer/sub count updates.");
                    return false;
                }

                response = await _kickHttp.SendAsync(KickReq());
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                return false;

            var channel = data[0];
            var changed = false;

            int subscriberCount = channel.TryGetProperty("active_subscribers_count", out var subsEl) && subsEl.TryGetInt32(out var subs)
                ? subs
                : 0;
            if (subscriberCount != _kickSubscriberCount)
            {
                _kickSubscriberCount = subscriberCount;
                changed = true;
            }

            int viewerCount = 0;
            bool kickLive = false;
            if (channel.TryGetProperty("stream", out var streamEl) &&
                streamEl.ValueKind == JsonValueKind.Object)
            {
                kickLive = streamEl.TryGetProperty("is_live", out var isLiveEl) && isLiveEl.GetBoolean();
                if (kickLive && streamEl.TryGetProperty("viewer_count", out var viewersEl) &&
                    viewersEl.TryGetInt32(out var kickViewers))
                    viewerCount = kickViewers;
            }

            if (viewerCount != _kickViewerCount)
            {
                _kickViewerCount = viewerCount;
                changed = true;
            }

            if (kickLive != KickIsLive) { KickIsLive = kickLive; changed = true; }

            // stream_title is a top-level field on the GetChannel response
            string kickTitle = channel.TryGetProperty("stream_title", out var stEl) ? stEl.GetString() ?? "" : "";
            string kickCat   = "";
            if (channel.TryGetProperty("category", out var catEl) && catEl.ValueKind == JsonValueKind.Object)
                kickCat = catEl.TryGetProperty("name", out var catNameEl) ? catNameEl.GetString() ?? "" : "";
            if (_kickStreamTitle    != kickTitle) { _kickStreamTitle    = kickTitle; changed = true; }
            if (_kickStreamCategory != kickCat)   { _kickStreamCategory = kickCat;   changed = true; }

            if (_kickAuthFailedToken != null)
            {
                _kickAuthFailedToken = null;
                KickAuthStatusChanged?.Invoke(true, "Kick API restored", "Kick viewer/sub count polling recovered after token refresh.");
            }

            return changed;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[StreamData] Kick channel poll: {Msg}", ex.Message);
            return false;
        }
    }

    private async Task<bool> TryRefreshKickTokenAsync()
    {
        var refreshToken = _tokens.Credentials.KickRefreshToken;
        var clientId = _tokens.Credentials.KickClientId;
        var clientSecret = _tokens.Credentials.KickClientSecret;
        if (string.IsNullOrWhiteSpace(refreshToken) || string.IsNullOrWhiteSpace(clientId))
            return false;

        try
        {
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret ?? "",
                ["refresh_token"] = refreshToken,
            });

            var response = await _kickHttp.PostAsync("https://id.kick.com/oauth/token", body);
            if (!response.IsSuccessStatusCode)
                return false;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var accessToken = root.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(accessToken))
                return false;

            var newRefreshToken = root.TryGetProperty("refresh_token", out var refreshEl)
                ? refreshEl.GetString()
                : refreshToken;

            _tokens.Credentials.KickAccessToken = accessToken;
            _tokens.Credentials.KickRefreshToken = newRefreshToken;
            _tokens.Save();
            KickAuthStatusChanged?.Invoke(true, "Kick login refreshed", "Kick API token refreshed successfully.");
            try { KickTokenRefreshed?.Invoke(); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[StreamData] Kick token refresh failed: {Msg}", ex.Message);
            return false;
        }
    }

    public async Task NoteFollowAsync(Platform platform, string displayName)
    {
        var changed = false;

        if (!string.IsNullOrWhiteSpace(displayName) && RecentFollower != displayName)
        {
            RecentFollower = displayName;
            PersistLastFollower(displayName, DateTimeOffset.UtcNow);
            changed = true;
        }

        if (platform == Platform.Kick)
        {
            if (_kickFollowerCountKnown)
            {
                _kickFollowerCount++;
                _settings.KickFollowerCountEstimate = _kickFollowerCount;
                _settings.Save();
                changed = true;
            }
        }
        else if (platform == Platform.Twitch)
        {
            _twitchFollowerCount++;
            changed = true;
        }

        if (changed)
        {
            RecalculateTotals();
            await PublishUpdatedAsync();
        }
    }

    public async Task NoteSubscribeAsync(Platform platform, int count = 1)
    {
        count = Math.Max(1, count);
        if (platform == Platform.Kick) _kickSubscriberCount += count;
        else if (platform == Platform.Twitch) _twitchSubscriberCount += count;
        else return;

        RecalculateTotals();
        await PublishUpdatedAsync();
    }

    public async Task SetKickFollowerCountFromUnsupportedAsync(int count)
    {
        count = Math.Max(0, count);
        var changed = !_kickFollowerCountKnown || _kickFollowerCount != count;
        _kickFollowerCountKnown = true;
        _kickFollowerCount = count;
        _settings.KickFollowerCountEstimate = count;
        _settings.Save();
        if (!changed) return;
        RecalculateTotals();
        await PublishUpdatedAsync();
    }

    public async Task IncrementKickFollowerCountFromUnsupportedAsync()
    {
        if (!_kickFollowerCountKnown) return;
        _kickFollowerCount++;
        _settings.KickFollowerCountEstimate = _kickFollowerCount;
        _settings.Save();
        RecalculateTotals();
        await PublishUpdatedAsync();
    }

    private void PersistLastFollower(string name, DateTimeOffset at)
    {
        _settings.LastFollowerName = name;
        _settings.LastFollowerAt   = at;
        _settings.Save();
    }

    // YouTube concurrent viewers. The chat service owns broadcast discovery and live state
    // (IsConnected = an active live chat is attached); we just read its broadcast id and poll
    // videos.list for concurrentViewers. No follower/sub count — YouTube's public API does not expose
    // a usable live equivalent (subscribers are the "follow" analogue and are hidden/rounded).
    private async Task<bool> PollYouTubeViewersAsync()
    {
        bool live = _youtube.IsConnected;
        var broadcastId = _youtube.ActiveBroadcastId;
        var token = _tokens.Credentials.YouTubeAccessToken;

        bool changed = false;
        if (!_settings.YouTubeActive || !live || string.IsNullOrWhiteSpace(broadcastId) || string.IsNullOrWhiteSpace(token))
        {
            if (YouTubeIsLive) { YouTubeIsLive = false; changed = true; }
            if (_youtubeViewerCount != 0) { _youtubeViewerCount = 0; changed = true; }
            return changed;
        }

        if (!YouTubeIsLive) { YouTubeIsLive = true; changed = true; }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.googleapis.com/youtube/v3/videos?part=liveStreamingDetails&id={Uri.EscapeDataString(broadcastId)}");
            req.Headers.Add("Authorization", $"Bearer {token}");
            using var resp = await _youtubeHttp.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return changed;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            int viewers = 0;
            if (doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0 &&
                items[0].TryGetProperty("liveStreamingDetails", out var lsd) &&
                lsd.TryGetProperty("concurrentViewers", out var cvEl))
            {
                // concurrentViewers is a STRING in the API ("123"), absent when not live.
                var cv = cvEl.GetString();
                if (!string.IsNullOrWhiteSpace(cv) && int.TryParse(cv, out var parsed))
                    viewers = parsed;
            }

            if (viewers != _youtubeViewerCount) { _youtubeViewerCount = viewers; changed = true; }
        }
        catch (Exception ex) { _logger.LogDebug("[StreamData] YouTube viewers poll: {Msg}", ex.Message); }

        return changed;
    }

    private void RecalculateTotals()
    {
        ViewerCount = Math.Max(0, _twitchViewerCount + _kickViewerCount + _youtubeViewerCount);
        FollowerCount = Math.Max(0, _twitchFollowerCount + (_kickFollowerCountKnown ? _kickFollowerCount : 0));
        SubscriberCount = Math.Max(0, _twitchSubscriberCount + _kickSubscriberCount);
    }

    private Task PublishUpdatedAsync()
        // Platform.System, NOT Twitch: this is the dashboard-totals aggregate for ALL platforms. Tagging
        // it as one platform meant a per-platform filter (or any platform-keyed consumer) could wrongly
        // drop/attribute it. Nothing here is dependent on a single platform being live.
        => _bus.PublishAsync(new StreamEvent(
            Platform.System, EventType.Achievement,
            new StreamUser("", "", ""),
            new Dictionary<string, object>
            {
                ["type"] = "StreamDataUpdated",
                ["viewerCount"] = ViewerCount,
                ["followerCount"] = FollowerCount,
                ["subscriberCount"] = SubscriberCount,
                ["twitchViewerCount"] = _twitchViewerCount,
                ["kickViewerCount"] = _kickViewerCount,
                ["youtubeViewerCount"] = _youtubeViewerCount,
                ["twitchFollowerCount"] = _twitchFollowerCount,
                ["kickFollowerCount"] = _kickFollowerCount,
                ["kickFollowerCountKnown"] = _kickFollowerCountKnown,
                ["twitchSubscriberCount"] = _twitchSubscriberCount,
                ["kickSubscriberCount"] = _kickSubscriberCount,
                ["twitchIsLive"] = TwitchIsLive,
                ["kickIsLive"] = KickIsLive,
                ["youtubeIsLive"] = YouTubeIsLive,
                ["streamStartedAt"] = (object?)StreamStartedAt ?? DBNull.Value,
            },
            DateTimeOffset.UtcNow));

    public string GetUptime()
    {
        if (StreamStartedAt is null) return "Offline";
        var elapsed = DateTimeOffset.UtcNow - StreamStartedAt.Value;
        if (elapsed.TotalHours >= 1)
            return $"{(int)elapsed.TotalHours}h {elapsed.Minutes:D2}m";
        return $"{elapsed.Minutes}m {elapsed.Seconds:D2}s";
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _pollTask; } catch { }
        _cts.Dispose();
    }
}
