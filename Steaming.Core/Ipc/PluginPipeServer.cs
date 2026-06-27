using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Steaming.Core.Services;
using System.IO;

namespace Steaming.Core.Ipc;

// Named pipe server — C++ OBS plugin connects here as a client.
// Wire protocol (matches pipe_client.cpp):
//   [1 byte]  PipeMessageType
//   [4 bytes] payload length (little-endian uint32)
//   [N bytes] payload
public class PluginPipeServer : IAsyncDisposable
{
    private const string PipeName = "steaming";

    private readonly ILogger<PluginPipeServer> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private CancellationTokenSource _cts = new();
    private Task _listenTask = Task.CompletedTask;
    private bool _receivedPluginHello;
    private bool _pluginCompatible;
    private string _pluginVersion = "";
    private int _pluginProtocolVersion;
    private string[] _pluginCapabilities = [];

    public bool IsConnected => _pipe?.IsConnected ?? false;
    public bool HasReceivedPluginHello => _receivedPluginHello;
    public bool IsPluginCompatible => _pluginCompatible;
    public string PluginVersion => _pluginVersion;
    public int PluginProtocolVersion => _pluginProtocolVersion;
    public IReadOnlyList<string> PluginCapabilities => _pluginCapabilities;
    public event Action<PipeMessageType, byte[]>? MessageReceived;
    // Fired on the pipe background thread immediately after the OBS plugin connects.
    // Do not call WriteFile/SendAsync directly from this handler — post to Task.Run.
    public event Func<Task>? Connected;

    public PluginPipeServer(ILogger<PluginPipeServer> logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _listenTask = ListenAsync(_cts.Token);
        LogDebug("PluginPipeServer started.");
    }

    public async Task SendAsync(PipeMessageType type, ReadOnlyMemory<byte> payload = default)
    {
        if (_pipe is null || !_pipe.IsConnected) { LogDebug($"Pipe not connected — drop {type}"); return; }

        var buf = new byte[5 + payload.Length];
        buf[0] = (byte)type;
        BitConverter.TryWriteBytes(buf.AsSpan(1), (uint)payload.Length);
        payload.Span.CopyTo(buf.AsSpan(5));

        // If a previous write is stuck (OBS not reading), don't block forever — drop this message.
        if (!await _writeLock.WaitAsync(TimeSpan.FromSeconds(3)))
        {
            LogDebug($"Pipe write lock timeout on {type} — OBS not reading, dropping.");
            return;
        }
        try
        {
            if (_pipe is null || !_pipe.IsConnected) return;
            LogDebug($"Pipe send. Type={type} Bytes={payload.Length}");
            using var writeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _pipe.WriteAsync(buf, writeCts.Token);
                await _pipe.FlushAsync(writeCts.Token);
            }
            catch (OperationCanceledException)
            {
                LogDebug($"Pipe write timeout on {type} — OBS stopped reading.");
            }
            catch (IOException ex)
            {
                LogDebug($"Pipe write IO error on {type}: {ex.Message}");
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous
            );

            _logger.LogInformation("[Pipe] Waiting for OBS plugin to connect...");
            LogDebug("Pipe waiting for OBS plugin connection.");

            try
            {
                await _pipe.WaitForConnectionAsync(ct);
                _logger.LogInformation("[Pipe] OBS plugin connected.");
                LogDebug("Pipe connected.");
                ResetPluginHandshakeState();
                await SendAsync(PipeMessageType.Hello, PipeHelloPayload.CreateAppHello().Serialize());
                // Fire on-connect sends in background — ReadLoopAsync must start immediately
                // so the pipe can service both reads and writes concurrently.
                // Awaiting here would block reads until all labels/goals are sent, which
                // deadlocks when C++ writes ChatSourceList back before we're reading.
                if (Connected != null)
                    _ = Task.Run(async () => {
                        try { await Connected.Invoke(); }
                        catch (Exception ex) { LogDebug($"Connected handler error: {ex.Message}"); }
                    });
                await ReadLoopAsync(_pipe, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("[Pipe] Connection lost: {Message}", ex.Message);
                LogDebug($"Pipe connection lost. Error={ex.Message}");
            }
            finally
            {
                await _pipe.DisposeAsync();
                _pipe = null;
            }
        }
    }

    private async Task ReadLoopAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var header = new byte[5];
        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            if (!await ReadExactAsync(pipe, header, ct)) break;

            var type = (PipeMessageType)header[0];
            var length = BitConverter.ToUInt32(header, 1);
            var payload = length > 0 ? new byte[length] : Array.Empty<byte>();

            if (length > 0 && !await ReadExactAsync(pipe, payload, ct)) break;

            if (type == PipeMessageType.Ping)
            {
                // Fire-and-forget: don't block the read loop waiting for the write lock.
                _ = SendAsync(PipeMessageType.Pong, default);
                continue;
            }

            if (type == PipeMessageType.Hello)
            {
                ProcessHello(payload);
                continue;
            }

            _logger.LogDebug("[Pipe] Received: {Type} ({Length} bytes)", type, length);
            LogDebug($"Pipe recv. Type={type} Bytes={length}");
            MessageReceived?.Invoke(type, payload);
        }
    }

    private static async Task<bool> ReadExactAsync(PipeStream pipe, byte[] buf, CancellationToken ct)
    {
        var total = 0;
        while (total < buf.Length)
        {
            var read = await pipe.ReadAsync(buf.AsMemory(total), ct);
            if (read == 0) return false;
            total += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { await _listenTask; } catch { }
        try { _cts.Dispose(); } catch (ObjectDisposedException) { }
        try { _writeLock.Dispose(); } catch (ObjectDisposedException) { }
    }

    public (string State, string Summary, string Details) GetPluginStatus()
    {
        if (!IsConnected)
            return ("Pending", "Waiting for OBS plugin", "Open OBS with the plugin loaded to establish the pipe connection.");

        if (!_receivedPluginHello)
            return ("Warning", "OBS plugin connected, awaiting handshake", "The plugin connected to the pipe but has not reported its version/protocol yet.");

        if (!_pluginCompatible)
            return (
                "Error",
                $"OBS plugin protocol mismatch (plugin v{_pluginVersion})",
                $"Plugin protocol={_pluginProtocolVersion}, app protocol={PipeHelloPayload.CurrentProtocolVersion}. Update the OBS plugin and desktop app together."
            );

        var capabilityText = _pluginCapabilities.Length == 0
            ? "no capabilities reported"
            : string.Join(", ", _pluginCapabilities);
        return (
            "Healthy",
            $"OBS plugin connected (v{_pluginVersion})",
            $"Pipe protocol verified. Plugin capabilities: {capabilityText}."
        );
    }

    private void LogDebug(string message)
    {
        try { DebugLogFile.Append(message); } catch { }
    }

    private void ResetPluginHandshakeState()
    {
        _receivedPluginHello = false;
        _pluginCompatible = false;
        _pluginVersion = "";
        _pluginProtocolVersion = 0;
        _pluginCapabilities = [];
    }

    private void ProcessHello(byte[] payload)
    {
        var hello = PipeHelloPayload.Deserialize(payload);
        if (hello is null)
        {
            _logger.LogWarning("[Pipe] Received invalid hello payload from OBS plugin.");
            LogDebug("Pipe hello invalid.");
            return;
        }

        if (!string.Equals(hello.Role, "plugin", StringComparison.OrdinalIgnoreCase))
        {
            LogDebug($"Pipe hello ignored. Role={hello.Role}");
            return;
        }

        _receivedPluginHello = true;
        _pluginVersion = string.IsNullOrWhiteSpace(hello.Version) ? "unknown" : hello.Version;
        _pluginProtocolVersion = hello.ProtocolVersion;
        _pluginCapabilities = hello.Capabilities ?? [];
        _pluginCompatible = hello.ProtocolVersion == PipeHelloPayload.CurrentProtocolVersion;

        if (_pluginCompatible)
        {
            _logger.LogInformation("[Pipe] OBS plugin hello accepted. Version={Version} Protocol={Protocol}", _pluginVersion, _pluginProtocolVersion);
            LogDebug($"Pipe hello accepted. PluginVersion={_pluginVersion} Protocol={_pluginProtocolVersion}");
        }
        else
        {
            _logger.LogWarning("[Pipe] OBS plugin protocol mismatch. Version={Version} PluginProtocol={PluginProtocol} AppProtocol={AppProtocol}", _pluginVersion, _pluginProtocolVersion, PipeHelloPayload.CurrentProtocolVersion);
            LogDebug($"Pipe hello mismatch. PluginVersion={_pluginVersion} PluginProtocol={_pluginProtocolVersion} AppProtocol={PipeHelloPayload.CurrentProtocolVersion}");
        }
    }
}
