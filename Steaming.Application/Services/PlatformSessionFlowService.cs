using Steaming.Core.Configuration;
using System.Security.Cryptography;
using System.Text;

namespace Steaming.Application.Services;

public sealed class PlatformSessionFlowService(PlatformAuthConfig platformAuth)
{
    public TwitchLoginRequest CreateTwitchLoginRequest()
    {
        var scopes = string.Join(" ",
            "chat:read", "chat:edit",
            "channel:read:subscriptions", "channel:manage:broadcast",
            "channel:read:redemptions", "channel:manage:redemptions",
            "bits:read",
            "moderator:read:chatters", "moderator:manage:banned_users",
            "moderator:manage:chat_messages", "moderator:manage:chat_settings",
            "moderator:manage:announcements", "moderator:read:followers",
            "user:read:email", "user:read:broadcast");

        var authUrl = "https://id.twitch.tv/oauth2/authorize" +
                      $"?client_id={platformAuth.TwitchClientId}" +
                      $"&redirect_uri={Uri.EscapeDataString(platformAuth.RedirectUri)}" +
                      "&response_type=token" +
                      $"&scope={Uri.EscapeDataString(scopes)}";

        return new TwitchLoginRequest(authUrl, platformAuth.RedirectUri, platformAuth.TwitchClientId);
    }

    public KickLoginRequest CreateKickLoginRequest()
    {
        if (!platformAuth.KickDirectLoginEnabled)
        {
            return new KickLoginRequest(false, null, null, null, null,
                "Kick login is temporarily disabled until the official bridge-based integration is implemented.");
        }

        var verifier   = GeneratePkceVerifier();
        var challenge  = GeneratePkceChallenge(verifier);
        var state      = Base64UrlEncode(Guid.NewGuid().ToByteArray());
        var scopes     = string.Join(" ",
            "user:read",
            "channel:read", "channel:write",
            "channel:rewards:read",
            "chat:write",
            "moderation:ban", "moderation:chat_message:manage");
        var authUrl = "https://id.kick.com/oauth/authorize" +
                      $"?client_id={platformAuth.KickClientId}" +
                      $"&redirect_uri={Uri.EscapeDataString(platformAuth.RedirectUri)}" +
                      "&response_type=code" +
                      $"&scope={Uri.EscapeDataString(scopes)}" +
                      $"&state={state}" +
                      $"&code_challenge={challenge}" +
                      "&code_challenge_method=S256";

        return new KickLoginRequest(true, authUrl, platformAuth.RedirectUri, verifier,
            platformAuth.KickClientId, null);
    }

    // Bot account login — same app client IDs, minimal scopes, user logs in as their bot account
    public TwitchLoginRequest CreateTwitchBotLoginRequest()
    {
        var scopes = string.Join(" ", "chat:read", "chat:edit");
        var authUrl = "https://id.twitch.tv/oauth2/authorize" +
                      $"?client_id={platformAuth.TwitchClientId}" +
                      $"&redirect_uri={Uri.EscapeDataString(platformAuth.RedirectUri)}" +
                      "&response_type=token" +
                      "&force_verify=true" +
                      $"&scope={Uri.EscapeDataString(scopes)}";
        return new TwitchLoginRequest(authUrl, platformAuth.RedirectUri, platformAuth.TwitchClientId);
    }

    public KickLoginRequest CreateKickBotLoginRequest()
    {
        if (!platformAuth.KickDirectLoginEnabled)
            return new KickLoginRequest(false, null, null, null, null, "Kick login is temporarily disabled.");

        var verifier  = GeneratePkceVerifier();
        var challenge = GeneratePkceChallenge(verifier);
        var state     = Base64UrlEncode(Guid.NewGuid().ToByteArray());
        var scopes    = string.Join(" ", "user:read", "chat:write");
        var authUrl   = "https://id.kick.com/oauth/authorize" +
                        $"?client_id={platformAuth.KickClientId}" +
                        $"&redirect_uri={Uri.EscapeDataString(platformAuth.RedirectUri)}" +
                        "&response_type=code" +
                        $"&scope={Uri.EscapeDataString(scopes)}" +
                        $"&state={state}" +
                        $"&code_challenge={challenge}" +
                        "&code_challenge_method=S256";
        return new KickLoginRequest(true, authUrl, platformAuth.RedirectUri, verifier,
            platformAuth.KickClientId, null);
    }

    public YouTubeLoginRequest CreateYouTubeLoginRequest()
    {
        var verifier  = GeneratePkceVerifier();
        var challenge = GeneratePkceChallenge(verifier);
        var state     = Base64UrlEncode(Guid.NewGuid().ToByteArray());
        var scopes    = "https://www.googleapis.com/auth/youtube.force-ssl";
        var authUrl   = "https://accounts.google.com/o/oauth2/v2/auth" +
                        $"?client_id={Uri.EscapeDataString(platformAuth.YouTubeClientId)}" +
                        $"&redirect_uri={Uri.EscapeDataString(platformAuth.RedirectUri)}" +
                        "&response_type=code" +
                        $"&scope={Uri.EscapeDataString(scopes)}" +
                        "&access_type=offline" +
                        "&prompt=consent" +
                        $"&state={state}" +
                        $"&code_challenge={challenge}" +
                        "&code_challenge_method=S256";
        return new YouTubeLoginRequest(authUrl, platformAuth.RedirectUri, verifier, platformAuth.YouTubeClientId);
    }

    public YouTubeLoginRequest CreateYouTubeBotLoginRequest()
    {
        var verifier  = GeneratePkceVerifier();
        var challenge = GeneratePkceChallenge(verifier);
        var state     = Base64UrlEncode(Guid.NewGuid().ToByteArray());
        var scopes    = "https://www.googleapis.com/auth/youtube.force-ssl";
        var authUrl   = "https://accounts.google.com/o/oauth2/v2/auth" +
                        $"?client_id={Uri.EscapeDataString(platformAuth.YouTubeClientId)}" +
                        $"&redirect_uri={Uri.EscapeDataString(platformAuth.RedirectUri)}" +
                        "&response_type=code" +
                        $"&scope={Uri.EscapeDataString(scopes)}" +
                        "&access_type=offline" +
                        "&prompt=consent" +
                        $"&state={state}" +
                        $"&code_challenge={challenge}" +
                        "&code_challenge_method=S256";
        return new YouTubeLoginRequest(authUrl, platformAuth.RedirectUri, verifier, platformAuth.YouTubeClientId);
    }

    private static string GeneratePkceVerifier()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string GeneratePkceChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record TwitchLoginRequest(string AuthUrl, string RedirectUri, string ClientId);

public sealed record KickLoginRequest(
    bool    IsEnabled,
    string? AuthUrl,
    string? RedirectUri,
    string? CodeVerifier,
    string? ClientId,
    string? DisabledMessage);

public sealed record YouTubeLoginRequest(
    string AuthUrl,
    string RedirectUri,
    string CodeVerifier,
    string ClientId);
