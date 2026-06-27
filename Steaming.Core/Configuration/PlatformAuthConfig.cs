namespace Steaming.Core.Configuration;

public sealed class PlatformAuthConfig
{
    private static readonly PlatformAuthSecretValues Secrets = PlatformAuthSecrets.Load();

    public string RedirectUri { get; init; } = Secrets.RedirectUri;
    public string TwitchClientId { get; init; } = Secrets.TwitchClientId;
    public string KickClientId { get; init; } = Secrets.KickClientId;
    public string KickClientSecret { get; init; } = Secrets.KickClientSecret;
    public string YouTubeClientId { get; init; } = Secrets.YouTubeClientId;
    public string YouTubeClientSecret { get; init; } = Secrets.YouTubeClientSecret;
    public bool KickDirectLoginEnabled { get; init; } = Secrets.KickDirectLoginEnabled;
}
