namespace Steaming.Core.Models;

// A channel-point reward the user can assign a custom alert to. Populated either manually or by a
// NON-DESTRUCTIVE auto-fetch from the platform APIs (Twitch Helix / Kick public API). The user's
// AssignedAlert is the authoritative link to which custom alert fires on redemption.
public class ChannelReward
{
    // Platform reward id (Kick ulid / Twitch reward id). May be empty for a manually-added entry
    // that hasn't been matched to a fetched reward yet.
    public string Id { get; set; } = "";

    // "Twitch" | "Kick" — rewards are tracked per platform (this app is dual-platform by identity).
    public string Platform { get; set; } = "";

    public string Title { get; set; } = "";
    public int Cost { get; set; }

    // Mirror of the platform's enabled flag — informational only, shown in the list.
    public bool Enabled { get; set; } = true;

    // The CustomAlerts key to fire when this reward is redeemed. null/empty = no assignment
    // (falls back to the generic RewardRedemption alert).
    public string? AssignedAlert { get; set; }
}
