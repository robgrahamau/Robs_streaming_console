using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Steaming.Core.Services;

// Minimal obs-websocket 5.x client — no third-party NuGet required.
// Supports: connect/auth, GetSceneList, SetCurrentProgramScene, GetCurrentProgramScene.
public class ObsWebSocketService : IAsyncDisposable
{
    public sealed record ObsInputInfo(string Name, string Kind);

    // EventSubscription bitmask (obs-websocket 5.x). Outputs (1<<6) covers StreamStateChanged.
    private const int EventSubscriptionOutputs = 1 << 6;

    private readonly ILogger<ObsWebSocketService> _logger;
    private ClientWebSocket? _ws;
    private CancellationTokenSource _cts = new();
    private Task _readTask = Task.CompletedTask;
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private int _reqId;

    // Auto-reconnect state.
    private string _lastUrl = "";
    private string _lastPassword = "";
    private volatile bool _userClosed;   // true => a deliberate Disconnect/Dispose, do NOT auto-reconnect
    private int _reconnecting;            // 0/1 guard so only one reconnect loop runs at a time

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    /// <summary>When true, a dropped connection (not a user-initiated disconnect) triggers a backoff reconnect loop.</summary>
    public bool AutoReconnect { get; set; }

    public event Action<bool>? ConnectionChanged;
    /// <summary>Fires with true when OBS starts streaming, false when it stops. Pushed via the Outputs event subscription.</summary>
    public event Action<bool>? StreamStateChanged;
    /// <summary>Human-readable status text emitted while the auto-reconnect loop is counting down / retrying.</summary>
    public event Action<string>? ReconnectStatusChanged;

    public ObsWebSocketService(ILogger<ObsWebSocketService> logger) => _logger = logger;

    public async Task ConnectAsync(string url, string password)
    {
        _userClosed = false;
        _lastUrl = url;
        _lastPassword = password;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _ws  = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(url), _cts.Token);

        var hello = await ReadJsonAsync();
        var d = hello.GetProperty("d");

        string auth = "";
        if (d.TryGetProperty("authentication", out var authData))
        {
            var challenge = authData.GetProperty("challenge").GetString() ?? "";
            var salt      = authData.GetProperty("salt").GetString()      ?? "";
            auth = BuildAuth(password, challenge, salt);
        }

        // Subscribe to Outputs events so StreamStateChanged is pushed to us.
        await SendRawAsync(new { op = 1, d = new { rpcVersion = 1, authentication = auth, eventSubscriptions = EventSubscriptionOutputs } });

        var identified = await ReadJsonAsync();
        if (identified.GetProperty("op").GetInt32() != 2)
            throw new InvalidOperationException("OBS WebSocket authentication failed — check password.");

        ConnectionChanged?.Invoke(true);
        _readTask = ReadLoopAsync(_cts.Token);
        _logger.LogInformation("[OBS WS] Connected to {Url}", url);

        // Push the current streaming state to listeners (covers both manual connect and auto-reconnect).
        _ = InitStreamStateAsync();
    }

    public void Disconnect()
    {
        _userClosed = true;
        _cts.Cancel();
    }

    /// <summary>
    /// Connect-on-startup helper. Tries an immediate connect; if it fails (e.g. OBS not yet
    /// running) and <see cref="AutoReconnect"/> is on, starts the backoff loop so the app
    /// connects automatically once OBS comes up. Never throws.
    /// </summary>
    public async Task TryConnectWithReconnectAsync(string url, string password)
    {
        _userClosed   = false;
        _lastUrl      = url;
        _lastPassword = password;
        try
        {
            await ConnectAsync(url, password);
        }
        catch (Exception ex)
        {
            _logger.LogInformation("[OBS WS] Initial auto-connect failed: {Message}", ex.Message);
            if (AutoReconnect)
                _ = ReconnectLoopAsync();
        }
    }

    private static string BuildAuth(string password, string challenge, string salt)
    {
        var step1   = SHA256.HashData(Encoding.UTF8.GetBytes(password + salt));
        var step1b64 = Convert.ToBase64String(step1);
        var step2   = SHA256.HashData(Encoding.UTF8.GetBytes(step1b64 + challenge));
        return Convert.ToBase64String(step2);
    }

    private async Task<JsonElement> SendRequestAsync(string requestType, object? requestData = null)
    {
        var id  = $"r{Interlocked.Increment(ref _reqId)}";
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;

        await SendRawAsync(new { op = 6, d = new { requestType, requestId = id, requestData = requestData ?? (object)new { } } });
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var msg = await ReadJsonAsync(ct);
                var op  = msg.GetProperty("op").GetInt32();
                if (op == 7 && msg.TryGetProperty("d", out var d))
                {
                    var id = d.GetProperty("requestId").GetString() ?? "";
                    TaskCompletionSource<JsonElement>? tcs;
                    lock (_pending)
                    {
                        _pending.TryGetValue(id, out tcs);
                        _pending.Remove(id);
                    }
                    if (tcs != null)
                    {
                        var rd = d.TryGetProperty("responseData", out var v) ? v : default;
                        tcs.TrySetResult(rd);
                    }
                }
                else if (op == 5 && msg.TryGetProperty("d", out var ed)) // Event
                {
                    var eventType = ed.TryGetProperty("eventType", out var et) ? et.GetString() : null;
                    if (eventType == "StreamStateChanged" && ed.TryGetProperty("eventData", out var data))
                    {
                        var active = data.TryGetProperty("outputActive", out var oa)
                                     && oa.ValueKind == JsonValueKind.True;
                        StreamStateChanged?.Invoke(active);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning("[OBS WS] Read error: {Message}", ex.Message); }

        ConnectionChanged?.Invoke(false);

        // Connection dropped without a deliberate Disconnect — start the backoff reconnect loop if enabled.
        if (!_userClosed && AutoReconnect)
            _ = ReconnectLoopAsync();
    }

    private async Task ReconnectLoopAsync()
    {
        // Only one reconnect loop at a time.
        if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0) return;
        try
        {
            var delayMs = 2000;
            while (!_userClosed && AutoReconnect && !IsConnected)
            {
                ReconnectStatusChanged?.Invoke($"OBS WS: Reconnecting in {delayMs / 1000}s…");
                try { await Task.Delay(delayMs); } catch { }
                if (_userClosed || !AutoReconnect) break;

                ReconnectStatusChanged?.Invoke("OBS WS: Reconnecting…");
                try
                {
                    await ConnectAsync(_lastUrl, _lastPassword);
                    return; // success — ConnectAsync started a fresh read loop
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[OBS WS] Reconnect attempt failed: {Message}", ex.Message);
                    delayMs = Math.Min(delayMs * 2, 30000); // cap backoff at 30s
                }
            }
        }
        finally { Interlocked.Exchange(ref _reconnecting, 0); }
    }

    private async Task InitStreamStateAsync()
    {
        try { StreamStateChanged?.Invoke(await GetStreamStatusAsync()); }
        catch (Exception ex) { _logger.LogWarning("[OBS WS] GetStreamStatus failed: {Message}", ex.Message); }
    }

    private async Task<JsonElement> ReadJsonAsync(CancellationToken ct = default)
    {
        var buf = new byte[65536];
        var acc = new System.IO.MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await _ws!.ReceiveAsync(new ArraySegment<byte>(buf), ct);
            acc.Write(buf, 0, r.Count);
        } while (!r.EndOfMessage);
        using var doc = JsonDocument.Parse(acc.GetBuffer().AsMemory(0, (int)acc.Length));
        return doc.RootElement.Clone();
    }

    private async Task SendRawAsync(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        await _ws!.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _cts.Token);
    }

    public async Task<List<string>> GetScenesAsync()
    {
        var resp = await SendRequestAsync("GetSceneList");
        var list = new List<string>();
        if (resp.ValueKind == JsonValueKind.Object && resp.TryGetProperty("scenes", out var arr))
            foreach (var s in arr.EnumerateArray())
                if (s.TryGetProperty("sceneName", out var n))
                    list.Add(n.GetString() ?? "");
        return list;
    }

    public async Task SetCurrentSceneAsync(string sceneName) =>
        await SendRequestAsync("SetCurrentProgramScene", new { sceneName });

    public async Task<string> GetCurrentSceneAsync()
    {
        var resp = await SendRequestAsync("GetCurrentProgramScene");
        return resp.TryGetProperty("currentProgramSceneName", out var n) ? n.GetString() ?? "" : "";
    }

    /// <summary>Returns true if OBS is currently streaming (GetStreamStatus.outputActive).</summary>
    public async Task<bool> GetStreamStatusAsync()
    {
        var resp = await SendRequestAsync("GetStreamStatus");
        return resp.ValueKind == JsonValueKind.Object
               && resp.TryGetProperty("outputActive", out var active)
               && active.ValueKind == JsonValueKind.True;
    }

    public async Task SetInputSettingsAsync(string inputName, Dictionary<string, object> inputSettings, bool overlay = true)
    {
        await SendRequestAsync("SetInputSettings", new SetInputSettingsRequest(inputName, inputSettings, overlay));
    }

    public async Task<List<ObsInputInfo>> GetInputsAsync()
    {
        var resp = await SendRequestAsync("GetInputList");
        var list = new List<ObsInputInfo>();
        if (resp.ValueKind != JsonValueKind.Object || !resp.TryGetProperty("inputs", out var arr))
            return list;

        foreach (var input in arr.EnumerateArray())
        {
            var name = input.TryGetProperty("inputName", out var n) ? n.GetString() ?? "" : "";
            var kind = input.TryGetProperty("inputKind", out var k) ? k.GetString() ?? "" : "";
            if (!string.IsNullOrWhiteSpace(name))
                list.Add(new ObsInputInfo(name, kind));
        }

        return list;
    }

    private sealed record SetInputSettingsRequest(
        [property: JsonPropertyName("inputName")] string InputName,
        [property: JsonPropertyName("inputSettings")] Dictionary<string, object> InputSettings,
        [property: JsonPropertyName("overlay")] bool Overlay);

    public async ValueTask DisposeAsync()
    {
        _userClosed = true;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { await _readTask; } catch { }
        try { _ws?.Dispose(); } catch (ObjectDisposedException) { }
        try { _cts.Dispose(); } catch (ObjectDisposedException) { }
    }
}
