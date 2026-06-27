using Steaming.Core.Models;

namespace Steaming.Application.ViewModels;

public sealed class ChatMessageItem : ViewModelBase
{
    private bool _showTimestamp;

    public ChatMessageItem(
        string userId, string username, string displayName,
        string messageId, string message, Platform platform,
        string color, DateTimeOffset timestamp, bool showTimestamp,
        string? prefixOverride = null)
    {
        UserId      = userId;
        Username    = username;
        DisplayName = displayName;
        MessageId   = messageId;
        Message     = message;
        Platform    = platform;
        Color       = color;
        Timestamp   = timestamp;
        PrefixOverride = prefixOverride;
        _showTimestamp = showTimestamp;
    }

    public string UserId      { get; }
    public string Username    { get; }
    public string DisplayName { get; }
    public string MessageId   { get; }
    public string Message     { get; }
    public Platform Platform  { get; }
    public string Color       { get; }
    public DateTimeOffset Timestamp { get; }
    public string? PrefixOverride { get; }

    public bool ShowTimestamp
    {
        get => _showTimestamp;
        set
        {
            if (_showTimestamp == value) return;
            _showTimestamp = value;
            Notify();
            Notify(nameof(FormattedText));
        }
    }

    public string FormattedText
    {
        get
        {
            var prefix = Platform switch
            {
                Platform.Twitch => "[T]",
                Platform.Kick => "[K]",
                Platform.YouTube => "[Y]",
                _ => "[?]",
            };
            if (!string.IsNullOrWhiteSpace(PrefixOverride))
                prefix = PrefixOverride!;
            var timestamp = ShowTimestamp ? $"[{Timestamp.ToLocalTime():HH:mm:ss}] " : "";
            return $"{timestamp}{prefix} {DisplayName}: {Message}";
        }
    }
}
