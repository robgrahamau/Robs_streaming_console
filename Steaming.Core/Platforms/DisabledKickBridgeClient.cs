namespace Steaming.Core.Platforms;

public sealed class DisabledKickBridgeClient : IKickBridgeClient
{
    public event Action<bool, string, string>? StatusChanged
    {
        add { }
        remove { }
    }

    public event Action? Reconnected
    {
        add { }
        remove { }
    }

    public event Action<string>? AuthRejected
    {
        add { }
        remove { }
    }

    public bool IsConfigured => false;
    public bool IsConnected => false;
    public string StatusSummary => "Bridge client not configured";
    public string StatusDetails => "Remote Kick bridge wiring has not been implemented yet in this app build.";

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> SendBootstrapAsync(KickBridgeSessionBootstrap bootstrap, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
    public Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
