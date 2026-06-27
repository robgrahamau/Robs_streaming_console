using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace Steaming.Core.Auth;

public class StoredCredentials
{
    public string? TwitchAccessToken { get; set; }
    public string? TwitchUsername    { get; set; }
    public string? TwitchChannel     { get; set; }
    public string? TwitchClientId    { get; set; }
    public string? TwitchUserId      { get; set; }
    public string? KickAccessToken   { get; set; }
    public string? KickRefreshToken  { get; set; }
    public string? KickUsername      { get; set; }
    public string? KickClientId      { get; set; }
    public string? KickClientSecret  { get; set; }
    public int     KickChatroomId    { get; set; }
    public string? YouTubeAccessToken { get; set; }
    public string? YouTubeRefreshToken { get; set; }
    public DateTimeOffset? YouTubeTokenExpiry { get; set; }
    public string? YouTubeChannelId { get; set; }
    public string? YouTubeChannelTitle { get; set; }
    public string? YouTubeClientId { get; set; }
    public string? YouTubeClientSecret { get; set; }
    // Bot account — separate Twitch/Kick account that sends chat on behalf of commands/timers
    public string? BotTwitchAccessToken { get; set; }
    public string? BotTwitchUsername    { get; set; }
    public string? BotKickAccessToken   { get; set; }
    public string? BotKickRefreshToken  { get; set; }
    public string? BotKickUsername      { get; set; }
    public string? BotYouTubeAccessToken { get; set; }
    public string? BotYouTubeRefreshToken { get; set; }
    public DateTimeOffset? BotYouTubeTokenExpiry { get; set; }
    public string? BotYouTubeChannelId { get; set; }
    public string? BotYouTubeUsername { get; set; }
}

[SupportedOSPlatform("windows")]
public class TokenStore
{
    private static readonly string DataDir  = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steaming");
    private static readonly string FilePath = Path.Combine(DataDir, "credentials.json");

    public StoredCredentials Credentials { get; private set; } = new();

    public void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var encrypted = File.ReadAllBytes(FilePath);
            var json = Encoding.UTF8.GetString(
                ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
            Credentials = JsonSerializer.Deserialize<StoredCredentials>(json) ?? new();
        }
        catch { Credentials = new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        var json = JsonSerializer.Serialize(Credentials);
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, encrypted);
    }
}
