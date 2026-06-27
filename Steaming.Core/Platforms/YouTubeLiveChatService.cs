using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Steaming.Core.Auth;
using Steaming.Core.Models;
using Steaming.Core.Services;
using Youtube.Api.V3;

namespace Steaming.Core.Platforms;

 [SupportedOSPlatform("windows")]
public sealed class YouTubeLiveChatService : IAsyncDisposable
{
    private static readonly HttpClient _http = new();
    private const int LoggedOutRetryMs = 5000;
    private const int OfflineBroadcastRetryMs = 120000;
    private const int LiveChatPollMinMs = 5000;
    private const int LiveChatPollMaxMs = 30000;
    private const int RetryAfterChatGoneMs = 30000;
    private const int RetryAfterForbiddenMs = 60000;
    private const int RetryAfterQuotaExceededMs = 300000;
    private const int RetryAfterGenericErrorMs = 30000;

    private readonly EventBus _bus;
    private readonly TokenStore _tokens;
    private readonly ILogger<YouTubeLiveChatService> _logger;

    private CancellationTokenSource _cts = new();
    private Task _loopTask = Task.CompletedTask;
    private string? _nextPageToken;
    private string? _liveChatId;
    private string? _lastBroadcastId;
    private bool _started;

    public bool IsConnected { get; private set; }
    public string ChannelTitle => _tokens.Credentials.YouTubeChannelTitle ?? "";
    // The live broadcast/video id currently attached, so StreamDataService can poll concurrent viewers
    // for it without re-resolving the broadcast (saves a quota call and keeps one source of truth).
    public string? ActiveBroadcastId => _lastBroadcastId;

    public event Action<bool, string, string>? StatusChanged;

    public YouTubeLiveChatService(
        EventBus bus,
        TokenStore tokens,
        ILogger<YouTubeLiveChatService> logger)
    {
        _bus = bus;
        _tokens = tokens;
        _logger = logger;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        DebugLogFile.Append("[YouTube] Start requested.");
        _cts = new CancellationTokenSource();
        _loopTask = RunAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        _started = false;
        _cts.Cancel();
        try { await _loopTask; } catch { }
        IsConnected = false;
        _nextPageToken = null;
        _liveChatId = null;
        _lastBroadcastId = null;
        DebugLogFile.Append("[YouTube] Stopped.");
    }

    public async Task<bool> SendMessageAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var useBotToken = !string.IsNullOrWhiteSpace(_tokens.Credentials.BotYouTubeAccessToken);
        var token = await EnsureValidAccessTokenAsync(bot: useBotToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            SetStatus(false, "YouTube login missing", "YouTube chat send was requested without a valid YouTube access token.");
            return false;
        }

        var liveChatId = _liveChatId;
        if (string.IsNullOrWhiteSpace(liveChatId))
        {
            // liveBroadcasts.list?mine=true must run as the owning broadcaster account, not an
            // optional bot account. A separate bot channel does not own the stream.
            var broadcasterToken = await EnsureValidAccessTokenAsync(bot: false);
            if (string.IsNullOrWhiteSpace(broadcasterToken))
            {
                SetStatus(false, "YouTube live chat unavailable", "The broadcaster YouTube login is missing or expired, so the active live chat could not be resolved.");
                return false;
            }

            liveChatId = await ResolveLiveChatIdAsync(broadcasterToken, CancellationToken.None, bot: false);
        }
        if (string.IsNullOrWhiteSpace(liveChatId))
        {
            SetStatus(false, "YouTube live chat unavailable", "No active YouTube live chat was found for the connected channel, so the message was not sent.");
            return false;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://www.googleapis.com/youtube/v3/liveChat/messages?part=snippet");
        req.Headers.Add("Authorization", $"Bearer {token}");
        var body = JsonSerializer.Serialize(new
        {
            snippet = new
            {
                liveChatId,
                type = "textMessageEvent",
                textMessageDetails = new
                {
                    messageText = message
                }
            }
        });
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        try
        {
            using var resp = await _http.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                token = await RefreshAndPersistAsync(bot: useBotToken);
                if (string.IsNullOrWhiteSpace(token))
                {
                    SetStatus(false, "YouTube login expired", "Refresh failed while sending a YouTube chat message. Reconnect YouTube from the Connections page.");
                    return false;
                }
                using var retry = new HttpRequestMessage(HttpMethod.Post,
                    "https://www.googleapis.com/youtube/v3/liveChat/messages?part=snippet");
                retry.Headers.Add("Authorization", $"Bearer {token}");
                retry.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var retryResp = await _http.SendAsync(retry);
                if (!retryResp.IsSuccessStatusCode)
                {
                    SetStatus(false, "YouTube send failed", await DescribeApiErrorAsync(retryResp));
                    return false;
                }
                retryResp.EnsureSuccessStatusCode();
                return true;
            }
            if (!resp.IsSuccessStatusCode)
            {
                SetStatus(false, "YouTube send failed", await DescribeApiErrorAsync(resp));
                return false;
            }
            resp.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[YouTube] SendMessage failed: {Msg}", ex.Message);
            SetStatus(false, "YouTube send failed", ex.Message);
            return false;
        }
    }

    public async Task<string?> GetActiveLiveChatIdAsync()
    {
        var token = await EnsureValidAccessTokenAsync();
        if (string.IsNullOrWhiteSpace(token)) return null;
        return _liveChatId ?? await ResolveLiveChatIdAsync(token, CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var token = await EnsureValidAccessTokenAsync();
                if (string.IsNullOrWhiteSpace(token))
                {
                    SetStatus(false, "Waiting for YouTube login", "YouTube chat will connect after YouTube login is completed.");
                    await Task.Delay(LoggedOutRetryMs, ct);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(_liveChatId))
                {
                    _liveChatId = await ResolveLiveChatIdAsync(token, ct);
                    if (string.IsNullOrWhiteSpace(_liveChatId))
                    {
                        IsConnected = false;
                        SetStatus(false, "YouTube offline", "No active YouTube live chat was found for the connected channel.");
                        await Task.Delay(OfflineBroadcastRetryMs, ct);
                        continue;
                    }
                    DebugLogFile.Append($"[YouTube] Attached liveChatId={_liveChatId} broadcastId={_lastBroadcastId ?? "(none)"}.");
                    _nextPageToken = null;
                }

                await StreamChatAsync(token, ct);
                IsConnected = true;
                SetStatus(true, "YouTube connected", "YouTube live chat is connected and events are flowing.");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (RpcException ex) when (!ct.IsCancellationRequested)
            {
                IsConnected = false;
                HandleStreamRpcException(ex);
                await Task.Delay(GetRpcRetryDelay(ex), ct);
            }
            catch (Exception ex)
            {
                IsConnected = false;
                _logger.LogWarning("[YouTube] Chat loop error: {Msg}", ex.Message);
                SetStatus(false, "YouTube chat error", ex.Message);
                await Task.Delay(RetryAfterGenericErrorMs, ct);
            }
        }
    }

    private async Task StreamChatAsync(string token, CancellationToken ct)
    {
        using var channel = GrpcChannel.ForAddress("https://youtube.googleapis.com");
        var client = new V3DataLiveChatMessageService.V3DataLiveChatMessageServiceClient(channel);
        var headers = new Metadata
        {
            { "authorization", "Bearer " + token }
        };
        var request = new LiveChatMessageListRequest
        {
            LiveChatId = _liveChatId ?? "",
            MaxResults = 200,
            ProfileImageSize = 88
        };
        request.Part.Add("id");
        request.Part.Add("snippet");
        request.Part.Add("authorDetails");
        if (!string.IsNullOrWhiteSpace(_nextPageToken))
            request.PageToken = _nextPageToken;

        using var call = client.StreamList(request, headers: headers, cancellationToken: ct);
        await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
        {
            _nextPageToken = string.IsNullOrWhiteSpace(response.NextPageToken) ? _nextPageToken : response.NextPageToken;
            IsConnected = true;
            SetStatus(true, "YouTube connected", "YouTube live chat is connected and events are flowing.");

            foreach (var item in response.Items)
                await PublishChatItemAsync(item);
        }
    }

    private async Task PublishChatItemAsync(LiveChatMessage item)
    {
        var snippet = item.Snippet;
        var author = item.AuthorDetails;
        if (snippet == null || author == null) return;

        var user = new StreamUser(
            author.ChannelId ?? "",
            author.DisplayName ?? "",
            author.DisplayName ?? "",
            author.ProfileImageUrl,
            IsMod: author.IsChatModerator,
            IsSubscriber: author.IsChatSponsor,
            IsVip: author.IsVerified,
            IsBroadcaster: author.IsChatOwner);

        var messageId = item.Id ?? "";
        var publishedAt = DateTimeOffset.TryParse(snippet.PublishedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto
            : DateTimeOffset.UtcNow;

        switch (snippet.Type)
        {
            case LiveChatMessageSnippet.Types.TypeWrapper.Types.Type.TextMessageEvent:
            {
                var message = snippet.DisplayMessage ?? "";
                await _bus.PublishAsync(new StreamEvent(Platform.YouTube, EventType.Chat, user,
                    new Dictionary<string, object>
                    {
                        ["message"] = message,
                        ["color"] = "#FFFFFF",
                        ["messageId"] = messageId,
                        ["isBroadcaster"] = user.IsBroadcaster,
                        ["isModerator"] = user.IsMod,
                        ["isSubscriber"] = user.IsSubscriber,
                        ["isVip"] = user.IsVip,
                        ["isHighlighted"] = false,
                        ["bits"] = 0,
                        ["subMonths"] = 0,
                        ["emotes"] = new List<Steaming.Core.Ipc.EmoteSegment>(),
                        ["badgePaths"] = new List<string>(),
                    }, publishedAt));
                break;
            }
            case LiveChatMessageSnippet.Types.TypeWrapper.Types.Type.SuperChatEvent:
            case LiveChatMessageSnippet.Types.TypeWrapper.Types.Type.SuperStickerEvent:
            {
                var amountMicros = (long)(snippet.SuperChatDetails?.AmountMicros ?? 0UL);
                if (amountMicros == 0) amountMicros = (long)(snippet.SuperStickerDetails?.AmountMicros ?? 0UL);
                var amountDisplay = snippet.SuperChatDetails?.AmountDisplayString ?? "";
                if (string.IsNullOrWhiteSpace(amountDisplay)) amountDisplay = snippet.SuperStickerDetails?.AmountDisplayString ?? "";
                var message = snippet.DisplayMessage ?? "";
                await _bus.PublishAsync(new StreamEvent(Platform.YouTube, EventType.Bits, user,
                    new Dictionary<string, object>
                    {
                        ["bits"] = (int)Math.Max(0, amountMicros / 1_000_000L),
                        ["amountDisplay"] = amountDisplay,
                        ["amountMicros"] = amountMicros,
                        ["message"] = message,
                    }, publishedAt));
                break;
            }
            case LiveChatMessageSnippet.Types.TypeWrapper.Types.Type.NewSponsorEvent:
            {
                await _bus.PublishAsync(new StreamEvent(Platform.YouTube, EventType.Subscribe, user,
                    new Dictionary<string, object> { ["months"] = 1 }, publishedAt));
                break;
            }
            case LiveChatMessageSnippet.Types.TypeWrapper.Types.Type.MemberMilestoneChatEvent:
            {
                var months = (int)(snippet.MemberMilestoneChatDetails?.MemberMonth ?? 0U);
                await _bus.PublishAsync(new StreamEvent(Platform.YouTube, EventType.Subscribe, user,
                    new Dictionary<string, object> { ["months"] = Math.Max(1, months) }, publishedAt));
                break;
            }
            case LiveChatMessageSnippet.Types.TypeWrapper.Types.Type.MembershipGiftingEvent:
            {
                var count = snippet.MembershipGiftingDetails?.GiftMembershipsCount ?? 0;
                await _bus.PublishAsync(new StreamEvent(Platform.YouTube, EventType.GiftSubscribe, user,
                    new Dictionary<string, object> { ["count"] = Math.Max(1, count), ["recipient"] = "YouTube chat" }, publishedAt));
                break;
            }
        }
    }

    private async Task<string?> ResolveLiveChatIdAsync(string token, CancellationToken ct, bool bot = false)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://www.googleapis.com/youtube/v3/liveBroadcasts?part=id,snippet,status&mine=true&broadcastType=all&maxResults=50");
        req.Headers.Add("Authorization", $"Bearer {token}");
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            token = await RefreshAndPersistAsync(bot) ?? "";
            if (string.IsNullOrWhiteSpace(token)) return null;
            using var retry = new HttpRequestMessage(HttpMethod.Get,
                "https://www.googleapis.com/youtube/v3/liveBroadcasts?part=id,snippet,status&mine=true&broadcastType=all&maxResults=50");
            retry.Headers.Add("Authorization", $"Bearer {token}");
            using var retryResp = await _http.SendAsync(retry, ct);
            if (!retryResp.IsSuccessStatusCode)
            {
                SetStatus(false, "YouTube broadcast lookup failed", await DescribeApiErrorAsync(retryResp));
                return null;
            }
            retryResp.EnsureSuccessStatusCode();
            return await ParseLiveChatIdAsync(retryResp, ct);
        }

        if (!resp.IsSuccessStatusCode)
        {
            SetStatus(false, "YouTube broadcast lookup failed", await DescribeApiErrorAsync(resp));
            return null;
        }
        return await ParseLiveChatIdAsync(resp, ct);
    }

    private void HandleStreamRpcException(RpcException ex)
    {
        _logger.LogWarning("[YouTube] streamList rpc error: {Code} {Msg}", ex.StatusCode, ex.Status.Detail);
        switch (ex.StatusCode)
        {
            case StatusCode.Unauthenticated:
                SetStatus(false, "YouTube login expired", "Refresh failed. Reconnect YouTube from the Connections page.");
                break;
            case StatusCode.NotFound:
                _liveChatId = null;
                _nextPageToken = null;
                SetStatus(false, "YouTube live chat unavailable", "The active YouTube live chat could not be found.");
                break;
            case StatusCode.FailedPrecondition:
                _liveChatId = null;
                _nextPageToken = null;
                SetStatus(false, "YouTube live chat ended", ex.Status.Detail);
                break;
            case StatusCode.ResourceExhausted:
                SetStatus(false, "YouTube quota exceeded", ex.Status.Detail);
                break;
            case StatusCode.PermissionDenied:
                SetStatus(false, "YouTube chat forbidden", ex.Status.Detail);
                break;
            case StatusCode.InvalidArgument:
                SetStatus(false, "YouTube chat error", ex.Status.Detail);
                break;
            default:
                SetStatus(false, "YouTube chat error", ex.Status.Detail);
                break;
        }
    }

    private static int GetRpcRetryDelay(RpcException ex) => ex.StatusCode switch
    {
        StatusCode.Unauthenticated => RetryAfterGenericErrorMs,
        StatusCode.NotFound => RetryAfterChatGoneMs,
        StatusCode.FailedPrecondition => RetryAfterChatGoneMs,
        StatusCode.ResourceExhausted => RetryAfterQuotaExceededMs,
        StatusCode.PermissionDenied => RetryAfterForbiddenMs,
        _ => RetryAfterGenericErrorMs,
    };

    private async Task<string?> ParseLiveChatIdAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.TryGetProperty("items", out var arr) ? arr : default;
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
        {
            _lastBroadcastId = null;
            return null;
        }

        foreach (var item in items.EnumerateArray())
        {
            var broadcastId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var lifeCycleStatus = item.TryGetProperty("status", out var statusEl) &&
                                  statusEl.TryGetProperty("lifeCycleStatus", out var lifeCycleEl)
                ? lifeCycleEl.GetString() ?? ""
                : "";
            var liveChatId = item.TryGetProperty("snippet", out var snippet) &&
                             snippet.TryGetProperty("liveChatId", out var chatEl)
                ? chatEl.GetString()
                : null;

            if (!string.Equals(lifeCycleStatus, "live", StringComparison.OrdinalIgnoreCase))
                continue;

            _lastBroadcastId = broadcastId;
            if (!string.IsNullOrWhiteSpace(liveChatId))
            {
                // Logged once on success only — broadcast discovery runs every ~2 min while offline and
                // would otherwise flood debug.log. State changes are surfaced through SetStatus (deduped).
                DebugLogFile.Append($"[YouTube] Selected live broadcastId={_lastBroadcastId ?? "(none)"} liveChatId={liveChatId}.");
                return liveChatId;
            }
        }

        _lastBroadcastId = null;
        return null;
    }

    private async Task<string?> EnsureValidAccessTokenAsync(bool bot = false)
    {
        var accessToken = bot ? _tokens.Credentials.BotYouTubeAccessToken : _tokens.Credentials.YouTubeAccessToken;
        var expiry = bot ? _tokens.Credentials.BotYouTubeTokenExpiry : _tokens.Credentials.YouTubeTokenExpiry;
        if (string.IsNullOrWhiteSpace(accessToken)) return null;
        if (expiry.HasValue && expiry.Value <= DateTimeOffset.UtcNow.AddMinutes(1))
            return await RefreshAndPersistAsync(bot);
        return accessToken;
    }

    private async Task<string?> RefreshAndPersistAsync(bool bot = false)
    {
        var refreshed = await RefreshTokenAsync(bot);
        if (refreshed == null) return null;

        if (bot)
        {
            _tokens.Credentials.BotYouTubeAccessToken  = refreshed.AccessToken;
            _tokens.Credentials.BotYouTubeRefreshToken = refreshed.RefreshToken ?? _tokens.Credentials.BotYouTubeRefreshToken;
            _tokens.Credentials.BotYouTubeTokenExpiry  = refreshed.ExpiryUtc;
            _tokens.Save();
        }
        else
        {
            _tokens.Credentials.YouTubeAccessToken  = refreshed.AccessToken;
            _tokens.Credentials.YouTubeRefreshToken = refreshed.RefreshToken ?? _tokens.Credentials.YouTubeRefreshToken;
            _tokens.Credentials.YouTubeTokenExpiry  = refreshed.ExpiryUtc;
            _tokens.Save();
        }

        return refreshed.AccessToken;
    }

    private async Task<RefreshedYouTubeToken?> RefreshTokenAsync(bool bot)
    {
        var refreshToken = bot ? _tokens.Credentials.BotYouTubeRefreshToken : _tokens.Credentials.YouTubeRefreshToken;
        var clientId = _tokens.Credentials.YouTubeClientId;
        var clientSecret = _tokens.Credentials.YouTubeClientSecret;
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
            var resp = await http.PostAsync("https://oauth2.googleapis.com/token", body);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            if (string.IsNullOrWhiteSpace(accessToken)) return null;
            var newRefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            DateTimeOffset? expiry = null;
            if (root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var seconds) && seconds > 0)
                expiry = DateTimeOffset.UtcNow.AddSeconds(seconds);
            return new RefreshedYouTubeToken(accessToken, newRefreshToken, expiry);
        }
        catch
        {
            return null;
        }
    }

    private bool? _lastStatusHealthy;
    private string _lastStatusSummary = "";

    private void SetStatus(bool healthy, string summary, string details)
    {
        // Dedupe: the gRPC receive loop calls this on every chat message. Without this guard it floods
        // debug.log (and re-fires the UI status) on every single line. Only log/raise on a real change.
        if (_lastStatusHealthy == healthy && _lastStatusSummary == summary)
            return;
        _lastStatusHealthy = healthy;
        _lastStatusSummary = summary;
        try { DebugLogFile.Append($"[YouTube] Status healthy={healthy} summary='{summary}' details='{details}'"); } catch { }
        try { StatusChanged?.Invoke(healthy, summary, details); } catch { }
    }

    private static async Task<string> DescribeApiErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync();
            var compact = CompactApiError(text);
            return string.IsNullOrWhiteSpace(compact)
                ? $"{(int)response.StatusCode} {response.ReasonPhrase}"
                : $"{(int)response.StatusCode} {response.ReasonPhrase}: {compact}";
        }
        catch
        {
            return $"{(int)response.StatusCode} {response.ReasonPhrase}";
        }
    }

    private static string CompactApiError(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var messageEl) ? messageEl.GetString() ?? "" : "";
                if (error.TryGetProperty("errors", out var errors) &&
                    errors.ValueKind == JsonValueKind.Array &&
                    errors.GetArrayLength() > 0)
                {
                    var first = errors[0];
                    var reason = first.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(reason) && !string.IsNullOrWhiteSpace(message))
                        return $"{reason}: {message}";
                }
                return message;
            }
        }
        catch
        {
        }

        return text.Length <= 200 ? text : text[..200];
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private sealed record RefreshedYouTubeToken(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiryUtc);
}
