using Steaming.Core.Models;

namespace Steaming.Core.Services;

public static class KickMetadataCache
{
    private static readonly object Gate = new();
    private static ChannelInfo? _lastKnown;

    public static ChannelInfo? Get()
    {
        lock (Gate)
        {
            return _lastKnown is null
                ? null
                : new ChannelInfo(_lastKnown.Title, _lastKnown.GameId, _lastKnown.GameName);
        }
    }

    public static void Set(ChannelInfo? info)
    {
        if (info is null) return;
        if (string.IsNullOrWhiteSpace(info.Title) && string.IsNullOrWhiteSpace(info.GameName)) return;

        lock (Gate)
            _lastKnown = new ChannelInfo(info.Title, info.GameId, info.GameName);
    }

    public static void Merge(string? title = null, string? gameId = null, string? gameName = null)
    {
        lock (Gate)
        {
            var current = _lastKnown ?? new ChannelInfo("", "", "");
            var merged = new ChannelInfo(
                string.IsNullOrWhiteSpace(title) ? current.Title : title,
                string.IsNullOrWhiteSpace(gameId) ? current.GameId : gameId,
                string.IsNullOrWhiteSpace(gameName) ? current.GameName : gameName);

            if (!string.IsNullOrWhiteSpace(merged.Title) || !string.IsNullOrWhiteSpace(merged.GameName))
                _lastKnown = merged;
        }
    }
}
