using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Steaming.Core.Ipc;

namespace Steaming.Core.Services;

// Manages BTTV, FFZ, and 7TV emotes for the current channel.
// Call InitializeForChannelAsync on connect; FindAndDownloadAsync on each message.
public sealed class ThirdPartyEmoteService
{
    public sealed record ProviderStatus(string Provider, bool Success, bool OptionalMissing, string Summary);
    public sealed record InitSummary(IReadOnlyList<ProviderStatus> Providers)
    {
        public bool HasWarnings => Providers.Any(p => !p.Success);
        public string Summary => string.Join(" | ", Providers.Select(p => $"{p.Provider}: {p.Summary}"));
        public string Details => string.Join("; ", Providers.Select(p => $"{p.Provider}: {p.Summary}"));
    }

    private sealed record FetchResult(bool Success, string? Content, int? StatusCode, string? ErrorMessage);

    public static readonly ThirdPartyEmoteService Instance = new();

    // Volatile reference — swapped atomically after a full reconnect load so
    // FindAndDownloadAsync always sees either the old complete set or the new
    // complete set, never a half-built dictionary.
    private volatile ConcurrentDictionary<string, (string id, string url)> _emotes
        = new(StringComparer.Ordinal);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private ThirdPartyEmoteService() { }

    public async Task<InitSummary> InitializeForChannelAsync(string twitchChannelId, string channelName)
    {
        // Build into a fresh dictionary; existing _emotes stays readable until swap.
        var next = new ConcurrentDictionary<string, (string id, string url)>(StringComparer.Ordinal);
        var providers = await Task.WhenAll(
            LoadBttvGlobalAsync(next),
            LoadBttvChannelAsync(next, twitchChannelId),
            LoadFfzChannelAsync(next, channelName),
            LoadSevenTvChannelAsync(next, twitchChannelId));
        // Atomic reference swap — no window where chat sees an empty dictionary.
        _emotes = next;
        return new InitSummary(providers);
    }

    private async Task<FetchResult> GetFetchResultAsync(string url)
    {
        try
        {
            using var resp = await _http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new FetchResult(false, null, (int)resp.StatusCode, null);
            return new FetchResult(true, await resp.Content.ReadAsStringAsync().ConfigureAwait(false), (int)resp.StatusCode, null);
        }
        catch (Exception ex)
        {
            return new FetchResult(false, null, null, ex.Message);
        }
    }

    private async Task<ProviderStatus> LoadBttvGlobalAsync(ConcurrentDictionary<string, (string id, string url)> target)
    {
        try
        {
            var result = await GetFetchResultAsync("https://api.betterttv.net/3/cached/emotes/global");
            if (!result.Success || string.IsNullOrWhiteSpace(result.Content))
                return new ProviderStatus("BTTV Global", false, false, result.ErrorMessage ?? $"HTTP {result.StatusCode}");

            int count = 0;
            using var doc = JsonDocument.Parse(result.Content);
            foreach (var em in doc.RootElement.EnumerateArray())
            {
                var id   = em.GetProperty("id").GetString()   ?? "";
                var code = em.GetProperty("code").GetString() ?? "";
                if (!string.IsNullOrEmpty(code))
                {
                    target[code] = (id, $"https://cdn.betterttv.net/emote/{id}/2x");
                    count++;
                }
            }
            return new ProviderStatus("BTTV Global", true, false, $"{count} emotes loaded");
        }
        catch (Exception ex)
        {
            return new ProviderStatus("BTTV Global", false, false, ex.Message);
        }
    }

    private async Task<ProviderStatus> LoadBttvChannelAsync(ConcurrentDictionary<string, (string id, string url)> target, string channelId)
    {
        if (string.IsNullOrEmpty(channelId))
            return new ProviderStatus("BTTV Channel", false, true, "No Twitch channel id");

        try
        {
            var result = await GetFetchResultAsync($"https://api.betterttv.net/3/cached/users/twitch/{channelId}");
            if (!result.Success || string.IsNullOrWhiteSpace(result.Content))
            {
                if (result.StatusCode == 404)
                    return new ProviderStatus("BTTV Channel", false, true, "No channel profile");
                return new ProviderStatus("BTTV Channel", false, false, result.ErrorMessage ?? $"HTTP {result.StatusCode}");
            }

            int count = 0;
            using var doc = JsonDocument.Parse(result.Content);
            foreach (var listKey in new[] { "channelEmotes", "sharedEmotes" })
            {
                if (!doc.RootElement.TryGetProperty(listKey, out var arr)) continue;
                foreach (var em in arr.EnumerateArray())
                {
                    var id   = em.GetProperty("id").GetString()   ?? "";
                    var code = em.GetProperty("code").GetString() ?? "";
                    if (!string.IsNullOrEmpty(code))
                    {
                        target[code] = (id, $"https://cdn.betterttv.net/emote/{id}/2x");
                        count++;
                    }
                }
            }
            return new ProviderStatus("BTTV Channel", true, false, $"{count} emotes loaded");
        }
        catch (Exception ex)
        {
            return new ProviderStatus("BTTV Channel", false, false, ex.Message);
        }
    }

    private async Task<ProviderStatus> LoadFfzChannelAsync(ConcurrentDictionary<string, (string id, string url)> target, string channelName)
    {
        if (string.IsNullOrEmpty(channelName))
            return new ProviderStatus("FFZ", false, true, "No channel name");
        try
        {
            var result = await GetFetchResultAsync($"https://api.frankerfacez.com/v1/room/{channelName}");
            if (!result.Success || string.IsNullOrWhiteSpace(result.Content))
            {
                if (result.StatusCode == 404)
                    return new ProviderStatus("FFZ", false, true, "No channel profile");
                return new ProviderStatus("FFZ", false, false, result.ErrorMessage ?? $"HTTP {result.StatusCode}");
            }

            int count = 0;
            using var doc = JsonDocument.Parse(result.Content);
            if (!doc.RootElement.TryGetProperty("sets", out var sets))
                return new ProviderStatus("FFZ", false, true, "No emote sets found");
            foreach (var set in sets.EnumerateObject())
            {
                if (!set.Value.TryGetProperty("emoticons", out var emoticons)) continue;
                foreach (var em in emoticons.EnumerateArray())
                {
                    var id   = em.GetProperty("id").GetInt32();
                    var name = em.GetProperty("name").GetString() ?? "";
                    if (!string.IsNullOrEmpty(name))
                    {
                        target[name] = (id.ToString(), $"https://cdn.frankerfacez.com/emote/{id}/2");
                        count++;
                    }
                }
            }
            return new ProviderStatus("FFZ", true, false, $"{count} emotes loaded");
        }
        catch (Exception ex)
        {
            return new ProviderStatus("FFZ", false, false, ex.Message);
        }
    }

    private async Task<ProviderStatus> LoadSevenTvChannelAsync(ConcurrentDictionary<string, (string id, string url)> target, string channelId)
    {
        if (string.IsNullOrEmpty(channelId))
            return new ProviderStatus("7TV", false, true, "No Twitch channel id");
        try
        {
            var result = await GetFetchResultAsync($"https://7tv.io/v3/users/twitch/{channelId}");
            if (!result.Success || string.IsNullOrWhiteSpace(result.Content))
            {
                if (result.StatusCode == 404)
                    return new ProviderStatus("7TV", false, true, "No channel profile");
                return new ProviderStatus("7TV", false, false, result.ErrorMessage ?? $"HTTP {result.StatusCode}");
            }

            int count = 0;
            using var doc = JsonDocument.Parse(result.Content);
            if (!doc.RootElement.TryGetProperty("emote_set", out var emoteSet))
                return new ProviderStatus("7TV", false, true, "No emote set found");
            if (!emoteSet.TryGetProperty("emotes", out var emotes))
                return new ProviderStatus("7TV", false, true, "No emotes found");
            foreach (var em in emotes.EnumerateArray())
            {
                var id   = em.GetProperty("id").GetString()   ?? "";
                var name = em.GetProperty("name").GetString() ?? "";
                if (!string.IsNullOrEmpty(name))
                {
                    target[name] = (id, $"https://cdn.7tv.app/emote/{id}/2x.webp");
                    count++;
                }
            }
            return new ProviderStatus("7TV", true, false, $"{count} emotes loaded");
        }
        catch (Exception ex)
        {
            return new ProviderStatus("7TV", false, false, ex.Message);
        }
    }

    // Scan message text for third-party emote words, download their images, return segments.
    // Results are merged with any existing Twitch native emote segments.
    public async Task<List<EmoteSegment>> FindAndDownloadAsync(string message, List<EmoteSegment> existing)
    {
        // Read the volatile reference once — consistent view for this entire call,
        // even if InitializeForChannelAsync swaps it mid-scan.
        var emotes = _emotes;
        if (emotes.IsEmpty) return existing;

        var result = new List<EmoteSegment>(existing);

        // Track char positions while scanning space-delimited tokens
        int pos = 0;
        var tokens = message.Split(' ');
        foreach (var token in tokens)
        {
            if (!string.IsNullOrEmpty(token) && emotes.TryGetValue(token, out var emote))
            {
                int start = pos, end = pos + token.Length - 1;
                // Skip if any existing emote overlaps this range
                bool covered = existing.Any(e => e.Start <= start && start <= e.End);
                if (!covered)
                {
                    var path = await EmoteCache.Instance
                        .GetOrDownloadAsync(emote.id, emote.url).ConfigureAwait(false);
                    result.Add(new EmoteSegment(start, end, path ?? ""));
                }
            }
            pos += token.Length + 1;
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    // Parse Kick emote markup [emote:ID:name] from content.
    // Returns the clean display text and a list of emote segments.
    public static async Task<(string cleanText, List<EmoteSegment> emotes)>
        ParseKickEmotesAsync(string content)
    {
        var emotes  = new List<EmoteSegment>();
        var clean   = new StringBuilder();
        var regex   = new Regex(@"\[emote:(\d+):([^\]]+)\]", RegexOptions.Compiled);
        int srcPos  = 0;

        foreach (Match m in regex.Matches(content))
        {
            // Copy text before this emote code
            clean.Append(content, srcPos, m.Index - srcPos);

            var emoteId   = m.Groups[1].Value;
            var emoteName = m.Groups[2].Value;

            int start = clean.Length;
            clean.Append(emoteName);
            int end = clean.Length - 1;

            // Kick emote CDN
            var url  = $"https://files.kick.com/emotes/{emoteId}/fullsize";
            var path = await EmoteCache.Instance.GetOrDownloadAsync(emoteId, url).ConfigureAwait(false);
            emotes.Add(new EmoteSegment(start, end, path ?? ""));

            srcPos = m.Index + m.Length;
        }
        clean.Append(content, srcPos, content.Length - srcPos);

        return (clean.ToString(), emotes);
    }
}
