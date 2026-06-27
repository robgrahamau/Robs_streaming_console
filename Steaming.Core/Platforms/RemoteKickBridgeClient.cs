using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Steaming.Core.Ipc;
using Steaming.Core.Models;
using Steaming.Core.Services;

namespace Steaming.Core.Platforms;

public sealed class RemoteKickBridgeClient : IKickBridgeClient
{
    private readonly AppSettings _settings;
    private readonly EventBus _bus;
    private readonly ILogger<RemoteKickBridgeClient> _logger;

    private ClientWebSocket? _ws;
    private CancellationTokenSource _cts = new();
    private Task _readTask = Task.CompletedTask;

    public event Action<bool, string, string>? StatusChanged;
    public event Action? Reconnected;
    public event Action<string>? AuthRejected;

    public bool IsConfigured => _settings.KickBridge.Enabled && !string.IsNullOrWhiteSpace(_settings.KickBridge.Host);
    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public string StatusSummary { get; private set; } = "Bridge client not configured";
    public string StatusDetails { get; private set; } = "Set a host/network name, port, and token to enable the remote Kick bridge.";

    public RemoteKickBridgeClient(AppSettings settings, EventBus bus, ILogger<RemoteKickBridgeClient> logger)
    {
        _settings = settings;
        _bus = bus;
        _logger = logger;
        RefreshConfiguredStatus();
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            RefreshConfiguredStatus();
            return;
        }

        if (IsConnected) return;

        await DisconnectAsync(cancellationToken);

        var cfg = _settings.KickBridge;
        var uri = BuildBridgeUri(cfg);
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {cfg.ClientToken.Trim()}");

        try
        {
            SetStatus(false, "Connecting to bridge", $"Opening WebSocket to {uri}.");
            await _ws.ConnectAsync(uri, cancellationToken);
            _readTask = ReadLoopAsync(_cts.Token);
            SetStatus(true, "Kick bridge connected", $"Connected to {cfg.Host}:{cfg.Port}{cfg.WebSocketPath}.");
            _logger.LogInformation("[KickBridge] Connected to {Uri}", uri);
        }
        catch (Exception ex)
        {
            SetStatus(false, "Kick bridge connection failed", ex.Message);
            _logger.LogWarning("[KickBridge] Connect failed: {Msg}", ex.Message);
            try { _ws.Dispose(); } catch { }
            _ws = null;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        try { _cts.Cancel(); } catch { }
        try { await _readTask; } catch { }

        if (_ws != null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect", closeCts.Token);
                }
            }
            catch { }
            try { _ws.Dispose(); } catch { }
            _ws = null;
        }

        RefreshConfiguredStatus();
    }

    public async Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        var cfg = _settings.KickBridge;
        if (!cfg.AllowOutboundChat)
        {
            LogDebug("Outbound Kick chat blocked on desktop: AllowOutboundChat is disabled.");
            return false;
        }
        if (!IsConnected || _ws == null)
        {
            LogDebug("Outbound Kick chat blocked on desktop: bridge is not connected.");
            return false;
        }

        var payload = JsonSerializer.Serialize(new
        {
            type = "kick.send_chat",
            message
        });

        var bytes = Encoding.UTF8.GetBytes(payload);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            LogDebug($"Sent kick.send_chat packet. chars={message.Length}");
            return true;
        }
        catch (WebSocketException ex)
        {
            DisposeSocket();
            SetStatus(false, "Kick bridge disconnected", ex.Message);
            _logger.LogWarning("[KickBridge] Send failed: {Msg}", ex.Message);
            LogDebug($"kick.send_chat failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SendBootstrapAsync(KickBridgeSessionBootstrap bootstrap, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _ws == null) return false;

        var payload = JsonSerializer.Serialize(new
        {
            type = "kick.bootstrap_session",
            accessToken = bootstrap.AccessToken,
            username = bootstrap.Username,
            broadcasterUserId = bootstrap.BroadcasterUserId,
            chatroomId = bootstrap.ChatroomId,
            allowOutboundChat = bootstrap.AllowOutboundChat,
        });

        var bytes = Encoding.UTF8.GetBytes(payload);
        LogDebug($"SendBootstrap: username='{bootstrap.Username}' broadcasterUserId={bootstrap.BroadcasterUserId} chatroomId={bootstrap.ChatroomId} allowOutbound={bootstrap.AllowOutboundChat}");
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            SetStatus(true, "Kick bridge bootstrapped", $"Session bound for {bootstrap.Username} ({bootstrap.BroadcasterUserId}).");
            return true;
        }
        catch (WebSocketException ex)
        {
            DisposeSocket();
            SetStatus(false, "Kick bridge disconnected", ex.Message);
            _logger.LogWarning("[KickBridge] Bootstrap send failed: {Msg}", ex.Message);
            LogDebug($"kick.bootstrap_session failed: {ex.Message}");
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        try { _cts.Dispose(); } catch { }
    }

    private void DisposeSocket()
    {
        if (_ws == null) return;
        try { _ws.Dispose(); } catch { }
        _ws = null;
    }

    private static Uri BuildBridgeUri(KickBridgeConfig cfg)
    {
        var scheme = cfg.UseTls ? "wss" : "ws";
        var path = string.IsNullOrWhiteSpace(cfg.WebSocketPath) ? "/ws/kick-bridge" : cfg.WebSocketPath.Trim();
        if (!path.StartsWith('/')) path = "/" + path;
        return new Uri($"{scheme}://{cfg.Host.Trim()}:{cfg.Port}{path}");
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        LogDebug("ReadLoop started.");
        var msgCount = 0;
        var isFirstConnection = true;
        var reconnectDelay = TimeSpan.FromSeconds(5);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!isFirstConnection)
            {
                // Auto-reconnect with exponential backoff
                LogDebug($"ReadLoop: reconnecting in {reconnectDelay.TotalSeconds}s...");
                SetStatus(false, "Kick bridge reconnecting", $"Connection lost. Retrying in {(int)reconnectDelay.TotalSeconds}s.");
                try { await Task.Delay(reconnectDelay, cancellationToken); } catch (OperationCanceledException) { break; }
                reconnectDelay = TimeSpan.FromSeconds(Math.Min(reconnectDelay.TotalSeconds * 2, 60));

                DisposeSocket();
                _ws = new ClientWebSocket();
                var cfg = _settings.KickBridge;
                _ws.Options.SetRequestHeader("Authorization", $"Bearer {cfg.ClientToken.Trim()}");
                var uri = BuildBridgeUri(cfg);
                try
                {
                    await _ws.ConnectAsync(uri, cancellationToken);
                    LogDebug("ReadLoop: reconnected. Firing Reconnected for re-bootstrap.");
                    SetStatus(true, "Kick bridge reconnected", $"Reconnected to {cfg.Host}:{cfg.Port}. Re-bootstrapping session.");
                    reconnectDelay = TimeSpan.FromSeconds(5);
                    try { Reconnected?.Invoke(); } catch { }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    LogDebug($"ReadLoop: reconnect failed: {ex.Message}");
                    DisposeSocket();
                    continue;
                }
            }
            isFirstConnection = false;

            // Read messages until connection dies. No receive timeout here —
            // aiohttp ping/pong frames are handled transparently by .NET and never
            // surface as messages, so a timeout would fire on healthy idle connections.
            var connectionDied = false;
            var buffer = new byte[65536];
            try
            {
                while (!cancellationToken.IsCancellationRequested && _ws?.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult? result = null;

                    do
                    {
                        result = await _ws.ReceiveAsync(buffer, cancellationToken);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            LogDebug("ReadLoop: server sent Close frame.");
                            DisposeSocket();
                            SetStatus(false, "Kick bridge disconnected", "Remote server closed the connection.");
                            connectionDied = true;
                            break;
                        }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (connectionDied) break;

                    msgCount++;
                    var json = Encoding.UTF8.GetString(ms.ToArray());
                    var preview = json.Length > 300 ? json[..300] + "…" : json;
                    LogDebug($"ReadLoop msg#{msgCount} type={result?.MessageType} len={json.Length}: {preview}");
                    await HandleIncomingAsync(json);
                }

                if (!connectionDied)
                    LogDebug($"ReadLoop: inner loop exited. WsState={_ws?.State} cancelled={cancellationToken.IsCancellationRequested}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                LogDebug($"ReadLoop exception: {ex.GetType().Name}: {ex.Message}");
                DisposeSocket();
                SetStatus(false, "Kick bridge lost", ex.Message);
                _logger.LogWarning("[KickBridge] Read error: {Msg}", ex.Message);
                connectionDied = true;
            }

            if (!connectionDied) break; // clean exit via cancellation
        }

        LogDebug($"ReadLoop ended. totalMsgs={msgCount}");
    }

    private async Task HandleIncomingAsync(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

            LogDebug($"HandleIncoming: type='{type}'");

            switch (type)
            {
                case "kick.event":
                    await PublishKickEventAsync(root);
                    break;
                case "kick.bridge_status":
                    HandleBridgeStatus(root);
                    break;
                default:
                    LogDebug($"HandleIncoming: unrecognised type='{type}' — ignored.");
                    break;
            }
        }
        catch (Exception ex)
        {
            LogDebug($"HandleIncoming parse error: {ex.GetType().Name}: {ex.Message}");
            _logger.LogDebug("[KickBridge] Ignored malformed payload: {Msg}", ex.Message);
        }
    }

    private void LogDebug(string msg)
    {
        try { DebugLogFile.Append($"[KickBridge] {msg}"); } catch { }
    }

    private async Task PublishKickEventAsync(JsonElement root)
    {
        var eventName = root.GetProperty("event").GetString() ?? "";
        LogDebug($"PublishKickEvent: eventName='{eventName}'");

        if (await TryHandleGenericKickEvent(eventName, root).ConfigureAwait(false))
        {
            LogDebug($"PublishKickEvent: handled by TryHandleGenericKickEvent.");
            return;
        }

        var eventType = eventName switch
        {
            "chat" => EventType.Chat,
            "follow" => EventType.Follow,
            "subscribe" => EventType.Subscribe,
            "gift_sub" => EventType.GiftSubscribe,
            "raid" => EventType.Raid,
            _ => EventType.Chat
        };

        if (eventName is not ("chat" or "follow" or "subscribe" or "gift_sub" or "raid"))
        {
            LogDebug($"Ignored unsupported generic Kick event '{eventName}'.");
            return;
        }

        LogDebug($"PublishKickEvent: eventName='{eventName}' eventType={eventType}");

        if (!root.TryGetProperty("user", out var userEl))
        {
            LogDebug("PublishKickEvent: missing 'user' property — drop.");
            return;
        }
        if (!root.TryGetProperty("data", out var dataEl))
        {
            LogDebug("PublishKickEvent: missing 'data' property — drop.");
            return;
        }

        var username = userEl.TryGetProperty("username", out var userNameEl) ? userNameEl.GetString() ?? "" : "";
        var rawMessage = GetString(dataEl, "message");
        LogDebug($"PublishKickEvent: user='{username}' message='{(rawMessage.Length > 60 ? rawMessage[..60] + "…" : rawMessage)}'");

        // Parse Kick [emote:ID:name] markup → clean text + emote segments with cached image paths
        var (cleanMessage, kickEmotes) = await ThirdPartyEmoteService.ParseKickEmotesAsync(rawMessage).ConfigureAwait(false);

        var user = new StreamUser(
            userEl.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
            username,
            userEl.TryGetProperty("displayName", out var displayNameEl) ? displayNameEl.GetString() ?? "" : "",
            null,
            GetBool(dataEl, "isModerator"),
            GetBool(dataEl, "isSubscriber"),
            GetBool(dataEl, "isVip"),
            GetBool(dataEl, "isBroadcaster"));

        var data = new Dictionary<string, object>
        {
            ["message"] = cleanMessage,
            ["messageId"] = GetString(dataEl, "messageId"),
            ["color"] = GetString(dataEl, "color"),
            ["isModerator"] = GetBool(dataEl, "isModerator"),
            ["isSubscriber"] = GetBool(dataEl, "isSubscriber"),
            ["isBroadcaster"] = GetBool(dataEl, "isBroadcaster"),
            ["isVip"] = GetBool(dataEl, "isVip"),
            ["isHighlighted"] = GetBool(dataEl, "isHighlighted"),
            ["bits"] = GetInt(dataEl, "bits"),
            ["subMonths"] = GetInt(dataEl, "subMonths"),
            ["recipient"] = GetString(dataEl, "recipient"),
            ["count"] = GetInt(dataEl, "count"),
            ["viewers"] = GetInt(dataEl, "viewers"),
            ["emotes"] = kickEmotes,
        };

        var timestamp = root.TryGetProperty("occurredAt", out var tsEl) && DateTimeOffset.TryParse(tsEl.GetString(), out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

        await _bus.PublishAsync(new StreamEvent(Platform.Kick, eventType, user, data, timestamp));
        LogDebug($"PublishKickEvent: published {eventType} for user='{username}'.");
    }

    private async Task<bool> TryHandleGenericKickEvent(string eventName, JsonElement root)
    {
        if (!root.TryGetProperty("data", out var dataEl))
            return false;

        var payloadEl = dataEl.TryGetProperty("payload", out var payloadProp) ? payloadProp : dataEl;

        if (eventName == "chat.message.sent")
        {
            await PublishGenericKickChatEvent(root, payloadEl).ConfigureAwait(false);
            return true;
        }

        if (eventName == "channel.reward.redemption.updated")
        {
            await PublishRewardRedemptionEventAsync(root, payloadEl).ConfigureAwait(false);
            return true;
        }

        if (eventName == "kicks.gifted")
        {
            await PublishKicksGiftedEventAsync(root, payloadEl).ConfigureAwait(false);
            return true;
        }

        if (eventName != "livestream.metadata.updated")
            return false;

        if (!payloadEl.TryGetProperty("metadata", out var metadataEl))
            return true;

        var title = metadataEl.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
        var gameId = "";
        var gameName = "";
        if (metadataEl.TryGetProperty("category", out var categoryEl))
        {
            if (categoryEl.TryGetProperty("id", out var idEl))
                gameId = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32().ToString() : idEl.GetString() ?? "";
            if (categoryEl.TryGetProperty("name", out var nameEl))
                gameName = nameEl.GetString() ?? "";
        }

        KickMetadataCache.Merge(title, gameId, gameName);
        LogDebug($"Cached Kick metadata from bridge event. title='{title}' category='{gameName}'");
        return true;
    }

    private async Task PublishGenericKickChatEvent(JsonElement root, JsonElement payloadEl)
    {
        if (!payloadEl.TryGetProperty("sender", out var senderEl))
            return;

        var username = senderEl.TryGetProperty("username", out var userNameEl) ? userNameEl.GetString() ?? "" : "";
        var identityEl = senderEl.TryGetProperty("identity", out var idEl) ? idEl : default;
        var isModerator = false;
        var isSubscriber = false;
        var isVip = false;
        var isBroadcaster = false;
        var subMonths = 0;

        if (identityEl.ValueKind == JsonValueKind.Object && identityEl.TryGetProperty("badges", out var badgesEl) && badgesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var badgeEl in badgesEl.EnumerateArray())
            {
                var type = badgeEl.TryGetProperty("type", out var typeEl) ? (typeEl.GetString() ?? "").Trim().ToLowerInvariant() : "";
                switch (type)
                {
                    case "moderator":
                        isModerator = true;
                        break;
                    case "subscriber":
                        isSubscriber = true;
                        if (badgeEl.TryGetProperty("count", out var countEl) && countEl.TryGetInt32(out var months))
                            subMonths = months;
                        break;
                    case "vip":
                        isVip = true;
                        break;
                    case "broadcaster":
                        isBroadcaster = true;
                        break;
                }
            }
        }

        if (!isBroadcaster &&
            payloadEl.TryGetProperty("broadcaster", out var broadcasterEl) &&
            senderEl.TryGetProperty("user_id", out var senderIdEl) &&
            broadcasterEl.TryGetProperty("user_id", out var broadcasterIdEl))
        {
            isBroadcaster = senderIdEl.ToString() == broadcasterIdEl.ToString();
        }

        var user = new StreamUser(
            senderEl.TryGetProperty("user_id", out var senderUserIdEl) ? senderUserIdEl.ToString() : "",
            username,
            username,
            null,
            isModerator,
            isSubscriber,
            isVip,
            isBroadcaster);

        var rawContent = payloadEl.TryGetProperty("content", out var contentEl) ? contentEl.GetString() ?? "" : "";
        var (cleanContent, kickEmotes) = await ThirdPartyEmoteService.ParseKickEmotesAsync(rawContent).ConfigureAwait(false);

        var data = new Dictionary<string, object>
        {
            ["message"] = cleanContent,
            ["messageId"] = payloadEl.TryGetProperty("message_id", out var messageIdEl) ? messageIdEl.GetString() ?? "" : "",
            ["color"] = identityEl.ValueKind == JsonValueKind.Object && identityEl.TryGetProperty("username_color", out var colorEl) ? colorEl.GetString() ?? "" : "",
            ["isModerator"] = isModerator,
            ["isSubscriber"] = isSubscriber,
            ["isBroadcaster"] = isBroadcaster,
            ["isVip"] = isVip,
            ["isHighlighted"] = false,
            ["bits"] = 0,
            ["subMonths"] = subMonths,
            ["recipient"] = "",
            ["count"] = 0,
            ["viewers"] = 0,
            ["emotes"] = kickEmotes,
        };

        var timestamp = root.TryGetProperty("occurredAt", out var tsEl) && DateTimeOffset.TryParse(tsEl.GetString(), out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

        await _bus.PublishAsync(new StreamEvent(Platform.Kick, EventType.Chat, user, data, timestamp)).ConfigureAwait(false);
        LogDebug($"Published Kick chat from documented webhook payload. user='{username}'");
    }

    private async Task PublishRewardRedemptionEventAsync(JsonElement root, JsonElement payloadEl)
    {
        // Kick's `channel.reward.redemption.updated` status is one of pending/accepted/rejected.
        // A brand-new redemption arrives as `pending`; an auto-fulfilled (skip-queue) reward
        // arrives as `accepted` with no prior pending. We want to surface the redemption once for
        // either, so only `rejected` is skipped. Dedupe by redemption id so a pending→accepted
        // pair for the SAME redemption does not fire the alert/activity twice.
        var status = payloadEl.TryGetProperty("status", out var statusEl) ? statusEl.GetString() ?? "" : "";
        if (status.Equals("rejected", StringComparison.OrdinalIgnoreCase))
        {
            LogDebug($"Ignored Kick reward redemption with status='{status}'.");
            return;
        }

        var redemptionId = payloadEl.TryGetProperty("id", out var redemptionIdEl) ? redemptionIdEl.GetString() ?? "" : "";
        if (!string.IsNullOrEmpty(redemptionId) && !MarkProcessedOnce(redemptionId))
        {
            LogDebug($"Ignored duplicate Kick reward redemption id='{redemptionId}' (status='{status}').");
            return;
        }

        if (!payloadEl.TryGetProperty("redeemer", out var redeemerEl))
        {
            LogDebug("Kick reward redemption missing redeemer payload.");
            return;
        }

        var username = redeemerEl.TryGetProperty("username", out var usernameEl) ? usernameEl.GetString() ?? "" : "";
        var user = new StreamUser(
            redeemerEl.TryGetProperty("user_id", out var userIdEl) ? userIdEl.ToString() : "",
            username,
            username);

        string rewardId = "";
        string rewardTitle = "";
        string rewardDescription = "";
        var rewardCost = 0;
        if (payloadEl.TryGetProperty("reward", out var rewardEl) && rewardEl.ValueKind == JsonValueKind.Object)
        {
            rewardId = rewardEl.TryGetProperty("id", out var rewardIdEl) ? rewardIdEl.GetString() ?? "" : "";
            rewardTitle = rewardEl.TryGetProperty("title", out var rewardTitleEl) ? rewardTitleEl.GetString() ?? "" : "";
            rewardDescription = rewardEl.TryGetProperty("description", out var rewardDescriptionEl) ? rewardDescriptionEl.GetString() ?? "" : "";
            if (rewardEl.TryGetProperty("cost", out var rewardCostEl) && rewardCostEl.TryGetInt32(out var parsedCost))
                rewardCost = parsedCost;
        }

        var data = new Dictionary<string, object>
        {
            ["rewardId"] = rewardId,
            ["rewardTitle"] = rewardTitle,
            ["rewardDescription"] = rewardDescription,
            ["rewardCost"] = rewardCost,
            ["userInput"] = payloadEl.TryGetProperty("user_input", out var userInputEl) ? userInputEl.GetString() ?? "" : "",
            ["redemptionId"] = redemptionId,
            ["status"] = status,
        };

        var timestamp = payloadEl.TryGetProperty("redeemed_at", out var redeemedAtEl) &&
                        DateTimeOffset.TryParse(redeemedAtEl.GetString(), out var redeemedAt)
            ? redeemedAt
            : DateTimeOffset.UtcNow;

        await _bus.PublishAsync(new StreamEvent(Platform.Kick, EventType.ChannelPointRedemption, user, data, timestamp)).ConfigureAwait(false);
        LogDebug($"Published Kick reward redemption for user='{username}' reward='{rewardTitle}'.");
    }

    // Kicks gifted (kicks.gifted) — Kick's monetary gifting (the equivalent of Twitch bits).
    // Payload shape: { broadcaster, sender, gift{amount,name,type,tier,message,...}, created_at }.
    private async Task PublishKicksGiftedEventAsync(JsonElement root, JsonElement payloadEl)
    {
        if (!payloadEl.TryGetProperty("sender", out var senderEl))
        {
            LogDebug("Kicks gifted event missing sender payload.");
            return;
        }

        var username = senderEl.TryGetProperty("username", out var usernameEl) ? usernameEl.GetString() ?? "" : "";
        var user = new StreamUser(
            senderEl.TryGetProperty("user_id", out var senderIdEl) ? senderIdEl.ToString() : "",
            username,
            username);

        var amount = 0;
        var giftName = "";
        var giftType = "";
        var giftTier = "";
        var giftMessage = "";
        if (payloadEl.TryGetProperty("gift", out var giftEl) && giftEl.ValueKind == JsonValueKind.Object)
        {
            if (giftEl.TryGetProperty("amount", out var amountEl) && amountEl.TryGetInt32(out var parsedAmount))
                amount = parsedAmount;
            giftName    = giftEl.TryGetProperty("name", out var nameEl)    ? nameEl.GetString() ?? ""    : "";
            giftType    = giftEl.TryGetProperty("type", out var typeEl)    ? typeEl.GetString() ?? ""    : "";
            giftTier    = giftEl.TryGetProperty("tier", out var tierEl)    ? tierEl.GetString() ?? ""    : "";
            giftMessage = giftEl.TryGetProperty("message", out var msgEl)  ? msgEl.GetString() ?? ""     : "";
        }

        var data = new Dictionary<string, object>
        {
            ["amount"]   = amount,    // number of Kicks gifted
            ["bits"]     = amount,    // alias so bits-style consumers/templates work
            ["giftName"] = giftName,
            ["giftType"] = giftType,
            ["giftTier"] = giftTier,
            ["message"]  = giftMessage,
        };

        var timestamp = root.TryGetProperty("occurredAt", out var tsEl) && DateTimeOffset.TryParse(tsEl.GetString(), out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

        await _bus.PublishAsync(new StreamEvent(Platform.Kick, EventType.KicksGifted, user, data, timestamp)).ConfigureAwait(false);
        LogDebug($"Published Kicks gifted: user='{username}' amount={amount} gift='{giftName}'.");
    }

    // Bounded one-shot dedupe used for webhook events the bridge may deliver more than once
    // (e.g. a redemption emitted as both pending and accepted). Returns true the first time an
    // id is seen, false afterwards. Called only from the single-threaded read loop.
    private readonly HashSet<string> _processedIds = new();
    private readonly Queue<string> _processedOrder = new();
    private bool MarkProcessedOnce(string id)
    {
        if (!_processedIds.Add(id)) return false;
        _processedOrder.Enqueue(id);
        if (_processedOrder.Count > 512)
            _processedIds.Remove(_processedOrder.Dequeue());
        return true;
    }

    private void HandleBridgeStatus(JsonElement root)
    {
        var summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "Kick bridge status updated" : "Kick bridge status updated";
        var details = root.TryGetProperty("details", out var d) ? d.GetString() ?? "" : "";
        var connected = root.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True;
        SetStatus(connected, summary, details);

        // The bridge reports Kick API auth failures (e.g. "Kick chat API returned 401:
        // Unauthorized") as status packets while the WebSocket stays connected — without
        // this check the desktop keeps showing green while every send/subscription fails.
        var text = summary + " " + details;
        if (text.Contains("401") || text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            LogDebug($"Bridge reported Kick auth rejection: {summary} | {details}");
            try { AuthRejected?.Invoke(string.IsNullOrWhiteSpace(details) ? summary : details); } catch { }
        }
    }

    private void RefreshConfiguredStatus()
    {
        if (IsConfigured)
            SetStatus(false, "Bridge configured", $"Target {_settings.KickBridge.Host}:{_settings.KickBridge.Port} is configured but not connected.");
        else
            SetStatus(false, "Bridge client not configured", "Set a host/network name, port, and token to enable the remote Kick bridge.");
    }

    private void SetStatus(bool connected, string summary, string details)
    {
        StatusSummary = summary;
        StatusDetails = details;
        StatusChanged?.Invoke(connected, summary, details);
    }

    private static string GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var prop) ? prop.GetString() ?? "" : "";

    private static bool GetBool(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.True;

    private static int GetInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var prop) && prop.TryGetInt32(out var value) ? value : 0;
}
