using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Steaming.Core.Models;
using Steaming.Core.Services;

namespace Steaming.Core.Platforms;

public readonly record struct KickChannelLookupResult(int ChatroomId, int? FollowersCount);

// OPT-IN Kick unsupported-data listener.
//
// Kick has NO official API for incoming raids/hosts — the only source is Kick's (unofficial) Pusher
// realtime socket, which is exactly what every alert app (Casterlabs Caffeinated, StreamElements, …)
// uses. The same unsupported browser-backed channel JSON also exposes `followers_count`, so this
// listener owns the opt-in unsupported Kick extras path: raids plus follower-total maintenance.
//
// It is best-effort and fully self-contained: every external call is guarded, a blocked chatroom-id
// lookup or a dropped socket never throws into the app, and it auto-reconnects. Disabled by default.
public sealed class KickRaidListener : IAsyncDisposable
{
    // Current Kick Pusher app (matches Casterlabs' maintained SDK). The repo's old KickAdapter key
    // (eb1d5f283081a78b932c / ap1) is stale — using the wrong key is why that path never worked.
    private const string PusherKey     = "32cbd69e4b950bf97679";
    private const string PusherCluster = "us2";
    private static string Endpoint =>
        $"wss://ws-{PusherCluster}.pusher.com/app/{PusherKey}?protocol=7&client=js&version=8.4.0&flash=false";

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private readonly EventBus _bus;
    private readonly ILogger<KickRaidListener> _logger;

    private CancellationTokenSource? _cts;
    private Task _runTask = Task.CompletedTask;
    private string _slug = "";

    public bool IsRunning { get; private set; }

    // Set by the WinUI layer: resolves chatroom_id + followers_count via a hidden WebView2 (real
    // browser → passes the Cloudflare block that 403s plain HttpClient). Tried before the HTTP fallback.
    public Func<string, CancellationToken, Task<KickChannelLookupResult>>? WebChannelResolver { get; set; }
    public event Action<int>? FollowerCountResolved;
    public event Action? FollowerObserved;

    private string _status = "Disabled";
    public string Status
    {
        get => _status;
        private set { if (_status == value) return; _status = value; StatusChanged?.Invoke(); }
    }
    public event Action? StatusChanged;

    public KickRaidListener(EventBus bus, ILogger<KickRaidListener> logger)
    {
        _bus = bus;
        _logger = logger;
    }

    private void LogDebug(string msg)
    {
        try { DebugLogFile.Append($"[KickRaid] {msg}"); } catch { }
    }

    // Idempotent. slug = the Kick channel slug (StoredCredentials.KickUsername).
    public void Start(string slug)
    {
        slug = (slug ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(slug))
        {
            Status = "No Kick channel — log in to Kick first";
            LogDebug("Start aborted — empty slug.");
            return;
        }
        if (IsRunning && string.Equals(slug, _slug, StringComparison.OrdinalIgnoreCase))
            return; // already running for this channel

        _ = RestartAsync(slug);
    }

    private async Task RestartAsync(string slug)
    {
        await StopAsync().ConfigureAwait(false);
        _slug = slug;
        _cts = new CancellationTokenSource();
        IsRunning = true;
        Status = "Starting…";
        LogDebug($"Starting raid listener for slug='{slug}'.");
        _runTask = RunLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { await _runTask.ConfigureAwait(false); } catch { }
        try { _cts?.Dispose(); } catch (ObjectDisposedException) { }
        _cts = null;
        IsRunning = false;
        if (Status != "Disabled") Status = "Stopped";
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        int chatroomId = 0;
        int resolveFails = 0;
        int backoffMs = 3000;

        while (!ct.IsCancellationRequested)
        {
            // ── Resolve the real chatroom id (the Cloudflare-prone step) ──────────
            if (chatroomId == 0)
            {
                var info = await ResolveChannelInfoAsync(_slug, ct).ConfigureAwait(false);
                chatroomId = info.ChatroomId;
                if (chatroomId == 0)
                {
                    resolveFails++;
                    if (resolveFails >= 5)
                    {
                        Status = "Kick raid detection unavailable — Kick blocked the channel lookup. Toggle off/on to retry.";
                        LogDebug("Giving up after 5 chatroom-id resolution failures.");
                        IsRunning = false;
                        return;
                    }
                    Status = $"Couldn't reach Kick to resolve channel (try {resolveFails}/5)…";
                    await SafeDelay(Math.Min(5000 * resolveFails, 30000), ct).ConfigureAwait(false);
                    continue;
                }
                if (info.FollowersCount is int followerCount && followerCount >= 0)
                {
                    try { FollowerCountResolved?.Invoke(followerCount); } catch { }
                }
                resolveFails = 0;
                LogDebug($"Resolved chatroom id={chatroomId} for slug='{_slug}'.");
            }

            // ── Connect + listen until the socket drops ───────────────────────────
            try
            {
                await ConnectAndListenAsync(chatroomId, ct).ConfigureAwait(false);
                backoffMs = 3000; // a clean run resets backoff
                if (!ct.IsCancellationRequested) Status = "Reconnecting…";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Status = "Disconnected — reconnecting…";
                LogDebug($"Listen error: {ex.GetType().Name}: {ex.Message}");
                backoffMs = Math.Min(backoffMs * 2, 30000);
            }

            await SafeDelay(backoffMs, ct).ConfigureAwait(false);
        }
    }

    private static async Task SafeDelay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    // GET kick.com/api/v2/channels/{slug} → chatroom.id. Returns 0 on ANY failure (Cloudflare HTML,
    // 403, timeout, missing field). Runs from the user's residential IP, which Kick blocks far less
    // than a datacenter/bridge IP. Tries v2 then v1.
    private async Task<KickChannelLookupResult> ResolveChannelInfoAsync(string slug, CancellationToken ct)
    {
        // Preferred: a real browser engine (WebView2) that passes Cloudflare. HttpClient 403s.
        if (WebChannelResolver != null)
        {
            try
            {
                Status = "Resolving Kick channel (browser)…";
                var info = await WebChannelResolver(slug, ct).ConfigureAwait(false);
                if (info.ChatroomId > 0) return info;
                LogDebug("WebView2 resolver returned 0 — trying HTTP fallback.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { LogDebug($"WebView2 resolver threw: {ex.Message}"); }
        }

        foreach (var url in new[]
                 {
                     $"https://kick.com/api/v2/channels/{slug}",
                     $"https://kick.com/api/v1/channels/{slug}",
                 })
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Add("User-Agent", BrowserUserAgent);
                http.DefaultRequestHeaders.Add("Accept", "application/json");

                using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    LogDebug($"Channel lookup {url} → HTTP {(int)resp.StatusCode}.");
                    continue;
                }

                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(body) || body.TrimStart().StartsWith("<"))
                {
                    LogDebug($"Channel lookup {url} → non-JSON (likely Cloudflare).");
                    continue;
                }

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("chatroom", out var chatroom) &&
                    chatroom.TryGetProperty("id", out var idEl) &&
                    idEl.TryGetInt32(out var id) && id > 0)
                {
                    var followersCount = TryGetInt(doc.RootElement, "followers_count")
                        ?? TryGetInt(doc.RootElement, "followersCount");
                    return new KickChannelLookupResult(id, followersCount);
                }

                LogDebug($"Channel lookup {url} → no chatroom.id in payload.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                LogDebug($"Channel lookup {url} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return default;
    }

    private async Task ConnectAndListenAsync(int chatroomId, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "https://kick.com");

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(15));
            await ws.ConnectAsync(new Uri(Endpoint), connectCts.Token).ConfigureAwait(false);
        }

        var sub = JsonSerializer.Serialize(new
        {
            @event = "pusher:subscribe",
            data = new { channel = $"chatrooms.{chatroomId}.v2" }
        });
        await SendAsync(ws, sub, ct).ConfigureAwait(false);
        Status = "Listening for Kick raids";
        LogDebug($"Subscribed to chatrooms.{chatroomId}.v2");

        var buf = new byte[32768];
        var acc = new System.IO.MemoryStream();
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult r;
            acc.SetLength(0);
            do
            {
                r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return;
                acc.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);

            var msg = Encoding.UTF8.GetString(acc.GetBuffer(), 0, (int)acc.Length);
            await HandleFrameAsync(ws, msg, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleFrameAsync(ClientWebSocket ws, string json, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("event", out var evtProp)) return;
            var evtName = evtProp.GetString();
            if (!root.TryGetProperty("data", out var dataProp)) return;
            var dataJson = dataProp.ValueKind == JsonValueKind.String ? dataProp.GetString() ?? "" : dataProp.GetRawText();

            // Keep the Pusher connection alive — drop these and the server disconnects us.
            if (evtName == "pusher:ping")
            {
                await SendAsync(ws, "{\"event\":\"pusher:pong\",\"data\":{}}", ct).ConfigureAwait(false);
                return;
            }

            if (evtName == "App\\Events\\FollowersUpdated")
            {
                HandleFollowersUpdated(dataJson);
                return;
            }

            // RAID ONLY for overlay/chat activity. FollowersUpdated only maintains the unsupported
            // follower total so we do not duplicate official Kick follow alerts from the bridge.
            if (evtName != "App\\Events\\StreamHostEvent") return;
            if (string.IsNullOrWhiteSpace(dataJson)) return;

            using var dataDoc = JsonDocument.Parse(dataJson);
            var data = dataDoc.RootElement;

            var host = (data.TryGetProperty("host_username", out var h) ? h.GetString() : null)
                       ?? (data.TryGetProperty("hostUsername", out var h2) ? h2.GetString() : null);
            if (string.IsNullOrWhiteSpace(host)) { LogDebug("StreamHostEvent had no host_username — ignored."); return; }

            int viewers = 0;
            if (data.TryGetProperty("number_viewers", out var v) && v.TryGetInt32(out var vc)) viewers = vc;
            else if (data.TryGetProperty("numberOfViewers", out var v2) && v2.TryGetInt32(out var vc2)) viewers = vc2;

            LogDebug($"RAID: host='{host}' viewers={viewers}");
            var user = new StreamUser("", host, host);
            await _bus.PublishAsync(new StreamEvent(Platform.Kick, EventType.Raid, user,
                new Dictionary<string, object> { ["viewers"] = viewers, ["count"] = viewers },
                DateTimeOffset.UtcNow)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogDebug($"Frame parse error: {ex.Message}");
        }
    }

    private void HandleFollowersUpdated(string dataJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dataJson)) return;
            using var dataDoc = JsonDocument.Parse(dataJson);
            var data = dataDoc.RootElement;

            var followerCount = TryGetInt(data, "followers_count")
                ?? TryGetInt(data, "followersCount")
                ?? TryGetInt(data, "count");
            if (followerCount is int fc && fc >= 0)
            {
                LogDebug($"FOLLOWERS UPDATED: count={fc}");
                try { FollowerCountResolved?.Invoke(fc); } catch { }
                return;
            }

            LogDebug("FOLLOWERS UPDATED: delta");
            try { FollowerObserved?.Invoke(); } catch { }
        }
        catch (Exception ex)
        {
            LogDebug($"FollowersUpdated parse error: {ex.Message}");
        }
    }

    private static int? TryGetInt(JsonElement el, params string[] path)
    {
        var cur = el;
        foreach (var key in path)
        {
            if (!cur.TryGetProperty(key, out cur)) return null;
        }
        return cur.TryGetInt32(out var value) ? value : null;
    }

    private static async Task SendAsync(ClientWebSocket ws, string message, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        await ws.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Status = "Disabled";
        await StopAsync().ConfigureAwait(false);
    }
}
