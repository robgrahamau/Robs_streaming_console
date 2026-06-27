using Steaming.Core.Services;

namespace Steaming.Application.Services;

public sealed class IntegrationConfigService(AppSettings settings)
{
    public void SaveObsConnection(string address, string password)
    {
        settings.ObsWebSocketAddress  = address.Trim();
        settings.ObsWebSocketPassword = password;
        settings.Save();
    }

    public void SaveObsAutoReconnect(bool enabled)
    {
        settings.ObsWebSocketAutoReconnect = enabled;
        settings.Save();
    }

    public void SaveWarnOnUnhealthyTwitch(bool enabled)
    {
        settings.WarnOnUnhealthyTwitch = enabled;
        settings.Save();
    }

    public void SaveWarnOnUnhealthyKick(bool enabled)
    {
        settings.WarnOnUnhealthyKick = enabled;
        settings.Save();
    }

    public string ObsAddress            => settings.ObsWebSocketAddress;
    public string ObsPassword           => settings.ObsWebSocketPassword;
    public bool   ObsAutoReconnect      => settings.ObsWebSocketAutoReconnect;
    public bool   WarnOnUnhealthyTwitch => settings.WarnOnUnhealthyTwitch;
    public bool   WarnOnUnhealthyKick   => settings.WarnOnUnhealthyKick;

    public KickBridgeConfig GetKickBridgeConfig() => new()
    {
        Enabled            = settings.KickBridge.Enabled,
        Host               = settings.KickBridge.Host,
        Port               = settings.KickBridge.Port,
        UseTls             = settings.KickBridge.UseTls,
        WebSocketPath      = settings.KickBridge.WebSocketPath,
        ClientToken        = settings.KickBridge.ClientToken,
        AllowOutboundChat  = settings.KickBridge.AllowOutboundChat,
    };

    public string SaveKickBridgeConfig(KickBridgeConfig config)
    {
        settings.KickBridge.Enabled           = config.Enabled;
        settings.KickBridge.Host              = config.Host.Trim();
        settings.KickBridge.Port              = config.Port;
        settings.KickBridge.UseTls            = config.UseTls;
        settings.KickBridge.WebSocketPath     = string.IsNullOrWhiteSpace(config.WebSocketPath)
                                                    ? "/ws/kick-bridge"
                                                    : config.WebSocketPath.Trim();
        settings.KickBridge.ClientToken       = config.ClientToken.Trim();
        settings.KickBridge.AllowOutboundChat = config.AllowOutboundChat;
        settings.Save();

        return config.Enabled && !string.IsNullOrWhiteSpace(config.Host)
            ? $"Target {config.Host}:{config.Port}{settings.KickBridge.WebSocketPath} is configured."
            : "Set a host/network name, port, and token to enable the remote Kick bridge.";
    }

    public bool KickBridgeAllowsOutboundChat => settings.KickBridge.AllowOutboundChat;
}
