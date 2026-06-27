using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Steaming.Core.Models;
using Steaming.Core.Services;

namespace Steaming.Core.Platforms;

// Connects to Twitch EventSub WebSocket and fires events through the EventBus.
// Handles: channel.follow, channel.cheer, channel.subscribe,
//          channel.subscription.gift, channel.raid
public class TwitchEventSubClient : IAsyncDisposable
{
    private const string WsUrl = "wss://eventsub.wss.twitch.tv/ws";

    private readonly EventBus _bus;
    private readonly ILogger<TwitchEventSubClient> _logger;
    private ClientWebSocket? _ws;
    private CancellationTokenSource _cts = new();
    private Task _readTask = Task.CompletedTask;

    private string? _token;
    private string? _clientId;
    private string? _broadcasterId;

    private void Publish(StreamEvent evt)
    {
        _ = _bus.PublishAsync(evt).ContinueWith(t =>
        {
            if (t.IsFaulted)
                LogDebug($"PublishAsync failed: {t.Exception?.GetBaseException().Message}");
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void LogDebug(string msg)
    {
        try { DebugLogFile.Append($"[EventSub] {msg}"); } catch { }
    }

    public TwitchEventSubClient(EventBus bus, ILogger<TwitchEventSubClient> logger)
    {
        _bus    = bus;
        _logger = logger;
    }

    public async Task ConnectAsync(string token, string clientId, string broadcasterId)
    {
        await TeardownAsync();

        _token         = token;
        _clientId      = clientId;
        _broadcasterId = broadcasterId;

        _cts      = new CancellationTokenSource();
        _ws       = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(WsUrl), _cts.Token);
        _readTask = ReadLoopAsync(_cts.Token);
        _logger.LogInformation("[EventSub] Connected.");
    }

    public Task DisconnectAsync() => TeardownAsync();

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buf = new byte[65536];
        var msg = new System.IO.MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    msg.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);

                var json = Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length);
                msg.SetLength(0);
                await ProcessMessageAsync(json);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning("[EventSub] Read error: {Msg}", ex.Message); }
    }

    private async Task ProcessMessageAsync(string json)
    {
        try
        {
            using var doc      = JsonDocument.Parse(json);
            var root           = doc.RootElement;
            var metadata       = root.GetProperty("metadata");
            var msgType        = metadata.GetProperty("message_type").GetString();
            var payload        = root.GetProperty("payload");

            switch (msgType)
            {
                case "session_welcome":
                    var sessionId = payload.GetProperty("session").GetProperty("id").GetString()!;
                    await SubscribeAllAsync(sessionId);
                    break;

                case "notification":
                    HandleNotification(payload);
                    break;

                case "session_keepalive":
                    break; // heartbeat — no action needed
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[EventSub] Parse error: {Msg}", ex.Message);
        }
    }

    private async Task SubscribeAllAsync(string sessionId)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_token}");
        http.DefaultRequestHeaders.Add("Client-Id", _clientId!);

        var subs = new (string type, string version, object condition)[]
        {
            ("channel.follow",                                     "2", new { broadcaster_user_id = _broadcasterId, moderator_user_id = _broadcasterId }),
            ("channel.cheer",                                      "1", new { broadcaster_user_id = _broadcasterId }),
            ("channel.subscribe",                                  "1", new { broadcaster_user_id = _broadcasterId }),
            ("channel.subscription.gift",                          "1", new { broadcaster_user_id = _broadcasterId }),
            ("channel.raid",                                       "1", new { to_broadcaster_user_id = _broadcasterId }),
            ("channel.channel_points_custom_reward_redemption.add","1", new { broadcaster_user_id = _broadcasterId }),
        };

        foreach (var (type, version, condition) in subs)
        {
            try
            {
                var body = JsonSerializer.Serialize(new
                {
                    type, version,
                    condition,
                    transport = new { method = "websocket", session_id = sessionId }
                });
                var resp = await http.PostAsync(
                    "https://api.twitch.tv/helix/eventsub/subscriptions",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                // 202 = created, 409 = already exists — both are fine
                var responseText = await resp.Content.ReadAsStringAsync();
                _logger.LogDebug("[EventSub] Subscribe {Type}: {Status}", type, resp.StatusCode);
                if (resp.StatusCode == System.Net.HttpStatusCode.Accepted ||
                    resp.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    LogDebug($"Subscribe {type}: {(int)resp.StatusCode} {resp.StatusCode}");
                }
                else
                {
                    _logger.LogWarning("[EventSub] Subscribe {Type} failed: {Status} {Body}", type, resp.StatusCode, responseText);
                    LogDebug($"Subscribe {type} failed: {(int)resp.StatusCode} {resp.StatusCode} {responseText}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[EventSub] Subscribe {Type} failed: {Msg}", type, ex.Message);
            }
        }
    }

    private void HandleNotification(JsonElement payload)
    {
        var subType = payload.GetProperty("subscription").GetProperty("type").GetString();
        var evt     = payload.GetProperty("event");
        LogDebug($"Notification received. subType={subType}");

        switch (subType)
        {
            case "channel.follow":
            {
                var user = new StreamUser(
                    evt.GetProperty("user_id").GetString()   ?? "",
                    evt.GetProperty("user_login").GetString() ?? "",
                    evt.GetProperty("user_name").GetString()  ?? "");
                LogDebug($"Publishing Follow event for {user.DisplayName}");
                Publish(new StreamEvent(Platform.Twitch, EventType.Follow, user,
                    new Dictionary<string, object>(), DateTimeOffset.UtcNow));
                break;
            }
            case "channel.cheer":
            {
                var user = new StreamUser(
                    evt.TryGetProperty("user_id",    out var uid)  ? uid.GetString()  ?? "" : "",
                    evt.TryGetProperty("user_login", out var ulog) ? ulog.GetString() ?? "" : "anonymous",
                    evt.TryGetProperty("user_name",  out var unm)  ? unm.GetString()  ?? "" : "Anonymous");
                var bits = evt.TryGetProperty("bits", out var b) ? b.GetInt32() : 0;
                Publish(new StreamEvent(Platform.Twitch, EventType.Bits, user,
                    new Dictionary<string, object> { ["bits"] = bits }, DateTimeOffset.UtcNow));
                break;
            }
            case "channel.subscribe":
            {
                var user = new StreamUser(
                    evt.GetProperty("user_id").GetString()   ?? "",
                    evt.GetProperty("user_login").GetString() ?? "",
                    evt.GetProperty("user_name").GetString()  ?? "");
                Publish(new StreamEvent(Platform.Twitch, EventType.Subscribe, user,
                    new Dictionary<string, object> { ["months"] = "1" }, DateTimeOffset.UtcNow));
                break;
            }
            case "channel.subscription.gift":
            {
                var user = new StreamUser(
                    evt.TryGetProperty("user_id",    out var uid)  ? uid.GetString()  ?? "" : "",
                    evt.TryGetProperty("user_login", out var ulog) ? ulog.GetString() ?? "" : "anonymous",
                    evt.TryGetProperty("user_name",  out var unm)  ? unm.GetString()  ?? "" : "Anonymous");
                Publish(new StreamEvent(Platform.Twitch, EventType.GiftSubscribe, user,
                    new Dictionary<string, object>(), DateTimeOffset.UtcNow));
                break;
            }
            case "channel.raid":
            {
                var user = new StreamUser(
                    evt.GetProperty("from_broadcaster_user_id").GetString()   ?? "",
                    evt.GetProperty("from_broadcaster_user_login").GetString() ?? "",
                    evt.GetProperty("from_broadcaster_user_name").GetString()  ?? "");
                var viewers = evt.TryGetProperty("viewers", out var v) ? v.GetInt32() : 0;
                Publish(new StreamEvent(Platform.Twitch, EventType.Raid, user,
                    new Dictionary<string, object> { ["viewers"] = viewers }, DateTimeOffset.UtcNow));
                break;
            }
            case "channel.channel_points_custom_reward_redemption.add":
            {
                var user = new StreamUser(
                    evt.TryGetProperty("user_id",    out var uid)  ? uid.GetString()  ?? "" : "",
                    evt.TryGetProperty("user_login", out var ulog) ? ulog.GetString() ?? "" : "anonymous",
                    evt.TryGetProperty("user_name",  out var unm)  ? unm.GetString()  ?? "" : "Anonymous");
                var rewardTitle = "";
                var rewardCost = 0;
                var rewardPrompt = "";
                if (evt.TryGetProperty("reward", out var reward) && reward.ValueKind == JsonValueKind.Object)
                {
                    if (reward.TryGetProperty("title", out var rt))
                        rewardTitle = rt.GetString() ?? "";
                    if (reward.TryGetProperty("cost", out var rc) && rc.TryGetInt32(out var cost))
                        rewardCost = cost;
                    if (reward.TryGetProperty("prompt", out var rp))
                        rewardPrompt = rp.GetString() ?? "";
                }
                var userInput = evt.TryGetProperty("user_input", out var ui) ? ui.GetString() ?? "" : "";
                Publish(new StreamEvent(Platform.Twitch, EventType.ChannelPointRedemption, user,
                    new Dictionary<string, object>
                    {
                        ["rewardTitle"] = rewardTitle,
                        ["rewardCost"] = rewardCost,
                        ["rewardPrompt"] = rewardPrompt,
                        ["userInput"] = userInput,
                    }, DateTimeOffset.UtcNow));
                break;
            }
        }
    }

    private async Task TeardownAsync()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { await _readTask; } catch { }
        _readTask = Task.CompletedTask;
        try { _ws?.Dispose(); } catch (ObjectDisposedException) { }
        _ws = null;
        try { _cts.Dispose(); } catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync() => await TeardownAsync();
}
