using System.Collections.Concurrent;
using System.Text.Json;

namespace Steaming.Core.Services;

// Downloads and caches actual Twitch badge images (global + channel).
// Returns local PNG paths to include in ChatPayload wire format.
public sealed class TwitchBadgeService
{
    public static readonly TwitchBadgeService Instance = new();

    // "setId/version" → local file path
    private readonly ConcurrentDictionary<string, string> _badges = new(StringComparer.Ordinal);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _cacheDir;

    private TwitchBadgeService()
    {
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Steaming", "badge_cache");
        Directory.CreateDirectory(_cacheDir);
    }

    // Call once on Twitch channel connect. Uses the broadcaster's OAuth token + client ID.
    public async Task InitializeAsync(string token, string clientId, string broadcasterId)
    {
        _badges.Clear();
        await Task.WhenAll(
            FetchBadgesAsync("https://api.twitch.tv/helix/chat/badges/global",
                             token, clientId),
            FetchBadgesAsync($"https://api.twitch.tv/helix/chat/badges?broadcaster_id={broadcasterId}",
                             token, clientId)
        );
    }

    private async Task FetchBadgesAsync(string url, string token, string clientId)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Authorization", $"Bearer {token}");
            req.Headers.Add("Client-Id", clientId);
            var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return;

            foreach (var badgeSet in data.EnumerateArray())
            {
                var setId = badgeSet.GetProperty("set_id").GetString() ?? "";
                if (!badgeSet.TryGetProperty("versions", out var versions)) continue;

                foreach (var ver in versions.EnumerateArray())
                {
                    var verId    = ver.GetProperty("id").GetString() ?? "";
                    var imageUrl = ver.TryGetProperty("image_url_2x", out var u2x)
                                   ? u2x.GetString() ?? ""
                                   : "";
                    if (string.IsNullOrEmpty(imageUrl)) continue;

                    var key  = $"{setId}/{verId}";
                    var path = Path.Combine(_cacheDir, $"{setId}_{verId}.png");

                    if (!File.Exists(path))
                    {
                        try
                        {
                            using var imageResp = await _http.GetAsync(imageUrl).ConfigureAwait(false);
                            if (!imageResp.IsSuccessStatusCode) continue;
                            var bytes = await imageResp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
                        }
                        catch { continue; }
                    }
                    _badges[key] = path;
                }
            }
        }
        catch { }
    }

    // Returns ordered badge image paths for the given badge list.
    // Returns an EMPTY list when no badge images are ready yet — the C++ renderer
    // will then use flag-based coloured pill fallbacks for the whole message.
    // Returns a non-empty list only once at least one badge image is available;
    // individual empty entries within the list mean "skip this badge slot".
    public List<string> GetBadgePaths(IEnumerable<KeyValuePair<string, string>> badges)
    {
        var result = new List<string>();
        foreach (var b in badges)
        {
            _badges.TryGetValue($"{b.Key}/{b.Value}", out var path);
            result.Add(path ?? "");
        }
        // Return empty list if nothing is ready — renderer falls back to pills
        return result.Any(p => !string.IsNullOrEmpty(p)) ? result : new List<string>();
    }
}
