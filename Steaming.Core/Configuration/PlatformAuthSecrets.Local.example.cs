namespace Steaming.Core.Configuration;

internal static partial class PlatformAuthSecrets
{
    static partial void ApplyLocalOverrides(ref PlatformAuthSecretValues values)
    {
        values = values with
        {
            RedirectUri = "http://localhost",
            TwitchClientId = "REPLACE_WITH_TWITCH_CLIENT_ID",
            KickClientId = "REPLACE_WITH_KICK_CLIENT_ID",
            KickClientSecret = "REPLACE_WITH_KICK_CLIENT_SECRET",
            YouTubeClientId = "REPLACE_WITH_YOUTUBE_CLIENT_ID",
            YouTubeClientSecret = "REPLACE_WITH_YOUTUBE_CLIENT_SECRET",
            KickDirectLoginEnabled = true,
        };
    }
}
