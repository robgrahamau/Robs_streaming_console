using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Steaming.Core.Models;
using Steaming.Core.Services;

namespace Steaming.Core.Platforms;

public class KickAdapter : IAsyncDisposable
{
    private const string PusherEndpoint =
        "wss://ws-ap1.pusher.com/app/eb1d5f283081a78b932c?protocol=7&client=js&version=7.6.0&flash=false";

    private readonly EventBus _bus;
    private readonly ILogger<KickAdapter> _logger;
    private ClientWebSocket? _ws;
    private CancellationTokenSource _cts = new();
    private Task _readTask = Task.CompletedTask;
    private string? _accessToken;
    private string? _botAccessToken;
    private string? _botUsername;
    private int _chatroomId;
    private string? _channelSlug;

    public bool    IsConnected  => _ws?.State == WebSocketState.Open;
    public bool    HasBotToken  => !string.IsNullOrWhiteSpace(_botAccessToken);
    public string? BotUsername  => _botUsername;

    private void LogDebug(string msg)
    {
        try { DebugLogFile.Append($"[Kick] {msg}"); } catch { }
    }

    private void Publish(StreamEvent evt)
    {
        _ = _bus.PublishAsync(evt).ContinueWith(t =>
        {
            if (t.IsFaulted)
                LogDebug($"PublishAsync failed: {t.Exception?.GetBaseException().Message}");
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public KickAdapter(EventBus bus, ILogger<KickAdapter> logger)
    {
        _bus    = bus;
        _logger = logger;
    }

    public async Task ConnectAsync(int chatroomId, string? accessToken = null, string? channelSlug = null)
    {
        _chatroomId   = chatroomId;
        _accessToken  = accessToken;
        _channelSlug  = channelSlug;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _ws  = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Origin", "https://kick.com");
        // No Authorization header on the WebSocket — Pusher uses its own auth, not Kick OAuth tokens.
        // The access token is only used for HTTP API calls (SendMessageAsync).

        await _ws.ConnectAsync(new Uri(PusherEndpoint), _cts.Token);
        _logger.LogInformation("[Kick] WebSocket connected.");

        var sub = JsonSerializer.Serialize(new
        {
            @event = "pusher:subscribe",
            data   = new { channel = $"chatrooms.{chatroomId}.v2" }
        });
        await SendAsync(sub);

        // Subscribe to channel-level events (host/raid, some follow variants)
        if (!string.IsNullOrEmpty(channelSlug))
        {
            var chSub = JsonSerializer.Serialize(new
            {
                @event = "pusher:subscribe",
                data   = new { channel = $"channel.{channelSlug}" }
            });
            await SendAsync(chSub);
        }

        _readTask = ReadLoopAsync(_cts.Token);
    }

    public void SetBotToken(string? accessToken, string? username)
    {
        _botAccessToken = accessToken;
        _botUsername    = username;
    }

    public void ClearBotToken()
    {
        _botAccessToken = null;
        _botUsername    = null;
    }

    public async Task SendMessageAsync(string message)
    {
        // Use bot token if available so messages appear from the bot account, not the broadcaster.
        var token = !string.IsNullOrWhiteSpace(_botAccessToken) ? _botAccessToken : _accessToken;
        if (string.IsNullOrEmpty(token) || _chatroomId == 0) return;
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            var body = JsonSerializer.Serialize(new
            {
                content              = message,
                type                 = "user",
                broadcaster_user_id  = _chatroomId
            });
            await http.PostAsync(
                "https://api.kick.com/public/v1/chat",
                new StringContent(body, Encoding.UTF8, "application/json"));
        }
        catch (Exception ex) { _logger.LogWarning("[Kick] SendMessage failed: {Msg}", ex.Message); }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buf = new byte[65536];
        var acc = new System.IO.MemoryStream();
        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogWarning("[Kick] Server closed: {Status} {Desc}",
                            _ws.CloseStatus, _ws.CloseStatusDescription);
                        return;
                    }
                    acc.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);

                var msg = Encoding.UTF8.GetString(acc.GetBuffer(), 0, (int)acc.Length);
                acc.SetLength(0);
                _logger.LogDebug("[Kick] Received: {Msg}", msg);
                ProcessMessage(msg);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning("[Kick] Read error: {Msg}", ex.Message); break; }
        }
        _logger.LogInformation("[Kick] Disconnected.");
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc  = JsonDocument.Parse(json);
            var root       = doc.RootElement;
            if (!root.TryGetProperty("event", out var evtProp)) return;
            var evtName    = evtProp.GetString();
            if (!root.TryGetProperty("data", out var dataProp)) return;

            var dataJson = dataProp.ValueKind == JsonValueKind.String
                ? dataProp.GetString()!
                : dataProp.GetRawText();
            using var dataDoc = JsonDocument.Parse(dataJson);
            var data = dataDoc.RootElement;

            if (evtName != "App\\Events\\ChatMessageEvent")
                LogDebug($"ProcessMessage evtName={evtName}");

            switch (evtName)
            {
                case "App\\Events\\ChatMessageEvent":              HandleChatMessage(data);        break;
                case "App\\Events\\FollowersUpdated":              HandleFollow(data);             break;
                case "App\\Events\\SubscriptionEvent":             HandleSubscription(data);       break;
                case "App\\Events\\GiftedSubscriptionsEvent":      HandleGiftedSubscription(data); break;
                case "App\\Events\\StreamHostEvent":               HandleStreamHost(data);         break;
                default:
                    if (evtName != null && !evtName.StartsWith("pusher"))
                        LogDebug($"ProcessMessage unhandled evtName={evtName}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[Kick] Parse error: {Msg}", ex.Message);
            LogDebug($"ProcessMessage parse error: {ex.Message}");
        }
    }

    private void HandleChatMessage(JsonElement data)
        => _ = HandleChatMessageAsync(data);

    private async Task HandleChatMessageAsync(JsonElement data)
    {
        var sender = data.GetProperty("sender");
        var user   = new StreamUser(
            sender.GetProperty("id").GetRawText(),
            sender.GetProperty("slug").GetString()     ?? "",
            sender.GetProperty("username").GetString() ?? "");

        // ── Badges ────────────────────────────────────────────────────────────
        bool isMod = false, isSub = false, isBroadcaster = false;
        int  subMonths = 0;
        if (sender.TryGetProperty("identity", out var identity) &&
            identity.TryGetProperty("badges", out var badgesEl) &&
            badgesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var badge in badgesEl.EnumerateArray())
            {
                var type = badge.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                switch (type.ToLowerInvariant())
                {
                    case "moderator":   isMod         = true; break;
                    case "broadcaster": isBroadcaster = true; break;
                    case "subscriber":
                        isSub = true;
                        if (badge.TryGetProperty("count", out var cnt))
                            subMonths = cnt.TryGetInt32(out int m) ? m : 1;
                        break;
                }
            }
        }

        // ── Parse Kick emote markup [emote:ID:name] from content ──────────────
        var rawContent = data.GetProperty("content").GetString() ?? "";
        var (cleanMessage, emoteSegments) =
            await Services.ThirdPartyEmoteService.ParseKickEmotesAsync(rawContent).ConfigureAwait(false);

        // ── Third-party text emotes (BTTV/FFZ/7TV) in clean message ───────────
        emoteSegments = await Services.ThirdPartyEmoteService.Instance
            .FindAndDownloadAsync(cleanMessage, emoteSegments).ConfigureAwait(false);

        var evtData = new Dictionary<string, object>
        {
            ["message"]       = cleanMessage,
            ["color"]         = TryGetString(sender, "identity", "color") ?? "#FFFFFF",
            ["messageId"]     = data.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            ["isBroadcaster"] = isBroadcaster,
            ["isModerator"]   = isMod,
            ["isSubscriber"]  = isSub,
            ["isVip"]         = false,
            ["isHighlighted"] = false,
            ["bits"]          = 0,
            ["subMonths"]     = subMonths,
            ["emotes"]        = emoteSegments,
            ["badgePaths"]    = new List<string>(),  // Kick has no Helix badge images
        };
        await _bus.PublishAsync(new StreamEvent(Platform.Kick, EventType.Chat, user, evtData, DateTimeOffset.UtcNow));
    }

    private void HandleFollow(JsonElement data)
    {
        var username = TryGetString(data, "username") ?? "someone";
        var user     = new StreamUser("", username, username);
        Publish(new StreamEvent(Platform.Kick, EventType.Follow, user,
            new Dictionary<string, object>(), DateTimeOffset.UtcNow));
    }

    private void HandleSubscription(JsonElement data)
    {
        var username = TryGetString(data, "username") ?? "someone";
        var user     = new StreamUser("", username, username);
        Publish(new StreamEvent(Platform.Kick, EventType.Subscribe, user,
            new Dictionary<string, object> { ["months"] = TryGetString(data, "months") ?? "1" },
            DateTimeOffset.UtcNow));
    }

    private void HandleGiftedSubscription(JsonElement data)
    {
        // gifters_username = who sent the gifts; gifted_usernames = array of recipients
        var gifter = TryGetString(data, "gifters_username") ?? "someone";
        var user   = new StreamUser("", gifter, gifter);
        var count  = data.TryGetProperty("number", out var n) && n.TryGetInt32(out int nc) ? nc : 1;
        // Use first recipient as "recipient" field; OverlayDispatcher uses {target} for this
        string recipient = "chat";
        if (data.TryGetProperty("gifted_usernames", out var arr) &&
            arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            recipient = arr[0].GetString() ?? "chat";
        Publish(new StreamEvent(Platform.Kick, EventType.GiftSubscribe, user,
            new Dictionary<string, object> { ["recipient"] = recipient, ["count"] = count },
            DateTimeOffset.UtcNow));
    }

    private void HandleStreamHost(JsonElement data)
    {
        // host_username = the channel hosting; number_viewers = viewer count
        var host    = TryGetString(data, "host_username") ?? "someone";
        var user    = new StreamUser("", host, host);
        var viewers = data.TryGetProperty("number_viewers", out var v) && v.TryGetInt32(out int vc) ? vc : 0;
        Publish(new StreamEvent(Platform.Kick, EventType.Raid, user,
            new Dictionary<string, object> { ["viewers"] = viewers },
            DateTimeOffset.UtcNow));
    }

    private static string? TryGetString(JsonElement el, params string[] path)
    {
        var cur = el;
        foreach (var key in path)
        {
            if (!cur.TryGetProperty(key, out cur)) return null;
        }
        return cur.ValueKind == JsonValueKind.String ? cur.GetString() : null;
    }

    private async Task SendAsync(string message)
    {
        if (_ws?.State != WebSocketState.Open) return;
        await _ws.SendAsync(Encoding.UTF8.GetBytes(message),
            WebSocketMessageType.Text, true, _cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { await _readTask; } catch { }
        if (_ws != null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", cts.Token);
                }
            }
            catch { }
            try { _ws.Dispose(); } catch (ObjectDisposedException) { }
        }
        try { _cts.Dispose(); } catch (ObjectDisposedException) { }
    }
}
