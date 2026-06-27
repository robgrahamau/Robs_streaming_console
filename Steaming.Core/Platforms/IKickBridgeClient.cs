namespace Steaming.Core.Platforms;

public interface IKickBridgeClient : IAsyncDisposable
{
    event Action<bool, string, string>? StatusChanged;
    // Fires after an automatic reconnect — subscribers should re-send bootstrap.
    event Action? Reconnected;
    // Fires when the bridge reports that Kick rejected the desktop session token
    // (401/unauthorized in a kick.bridge_status packet). The WebSocket itself is
    // still up — subscribers should surface an error and re-send bootstrap with
    // a fresh token.
    event Action<string>? AuthRejected;

    bool IsConfigured { get; }
    bool IsConnected { get; }
    string StatusSummary { get; }
    string StatusDetails { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<bool> SendBootstrapAsync(KickBridgeSessionBootstrap bootstrap, CancellationToken cancellationToken = default);
    Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken = default);
}
