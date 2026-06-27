namespace Steaming.Core.Models;

// Real streaming platforms, plus System for app-internal control/aggregate messages that belong to
// NO single platform (e.g. the StreamDataUpdated dashboard-totals broadcast). Nothing in a dual/triple
// platform app should ever be keyed to one platform's identity — internal events use System.
public enum Platform { Twitch, Kick, Steam, YouTube, System }

public enum EventType
{
    Chat,
    Follow,
    Subscribe,
    GiftSubscribe,
    Bits,
    Raid,
    ChannelPointRedemption,
    KicksGifted,
    HypeTrain,
    Poll,
    Prediction,
    Achievement,
    StreamHost,
}

public record StreamUser(
    string Id,
    string Username,
    string DisplayName,
    string? AvatarUrl = null,
    bool IsMod = false,
    bool IsSubscriber = false,
    bool IsVip = false,
    bool IsBroadcaster = false
);

public record StreamEvent(
    Platform Platform,
    EventType Type,
    StreamUser User,
    IReadOnlyDictionary<string, object> Data,
    DateTimeOffset Timestamp
);
