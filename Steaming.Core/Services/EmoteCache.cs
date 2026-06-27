using System.Collections.Concurrent;

namespace Steaming.Core.Services;

// Downloads and caches emote images to disk with correct file extension per content-type.
// Thread-safe: concurrent requests for the same emote share one download Task.
public sealed class EmoteCache
{
    public static readonly EmoteCache Instance = new();

    private readonly string _dir;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    // emoteId → resolved local path (null means permanently failed)
    private readonly ConcurrentDictionary<string, string?> _resolved = new();
    // emoteId → in-flight download Task (removed on failure so next call retries)
    private readonly ConcurrentDictionary<string, Task<string?>> _downloads = new();

    // Fired on the thread pool whenever a new image finishes downloading.
    // The chat source subscribes to this and marks itself dirty so existing
    // messages redraw with the newly available emote/badge images.
    public event Action? OnDownloadComplete;

    private EmoteCache()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Steaming", "emote_cache");
        Directory.CreateDirectory(_dir);
    }

    // Returns local file path when ready, or null if download fails.
    // Concurrent callers for the same emoteId share one Task.
    public Task<string?> GetOrDownloadAsync(string emoteId, string url)
    {
        if (string.IsNullOrEmpty(emoteId) || string.IsNullOrEmpty(url))
            return Task.FromResult<string?>(null);

        // Already resolved on a previous call
        if (_resolved.TryGetValue(emoteId, out var cached))
            return Task.FromResult(cached);

        // Share an in-flight download — prevents concurrent writes for the same emote
        return _downloads.GetOrAdd(emoteId, id => DownloadAsync(id, url));
    }

    private async Task<string?> DownloadAsync(string emoteId, string url)
    {
        // Check disk first — any extension we might have saved previously
        foreach (var ext in new[] { ".png", ".gif", ".webp" })
        {
            var existing = Path.Combine(_dir, $"{emoteId}{ext}");
            if (File.Exists(existing))
            {
                _resolved[emoteId] = existing;
                return existing;
            }
        }

        try
        {
            using var response = await _http.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // Use the actual content-type to decide the file extension
            var mime = response.Content.Headers.ContentType?.MediaType ?? "image/png";
            var ext  = mime switch
            {
                "image/gif"  => ".gif",
                "image/webp" => ".webp",
                _            => ".png",
            };

            var path  = Path.Combine(_dir, $"{emoteId}{ext}");
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);

            _resolved[emoteId] = path;
            _downloads.TryRemove(emoteId, out _);
            // Notify subscribers (chat source) so existing messages redraw with this image
            OnDownloadComplete?.Invoke();
            return path;
        }
        catch
        {
            // Remove from in-flight so the next message can retry
            _downloads.TryRemove(emoteId, out _);
            return null;
        }
    }
}
