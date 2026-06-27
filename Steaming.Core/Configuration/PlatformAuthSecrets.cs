namespace Steaming.Core.Configuration;

internal readonly record struct PlatformAuthSecretValues(
    string RedirectUri,
    string TwitchClientId,
    string KickClientId,
    string KickClientSecret,
    string YouTubeClientId,
    string YouTubeClientSecret,
    bool KickDirectLoginEnabled);

internal static partial class PlatformAuthSecrets
{
    public static PlatformAuthSecretValues Load()
    {
        var values = new PlatformAuthSecretValues(
            RedirectUri: "http://localhost",
            TwitchClientId: "",
            KickClientId: "",
            KickClientSecret: "",
            YouTubeClientId: "",
            YouTubeClientSecret: "",
            KickDirectLoginEnabled: true);

        ApplyLocalOverrides(ref values);
        return values;
    }

    static partial void ApplyLocalOverrides(ref PlatformAuthSecretValues values);
}
