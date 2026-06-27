using Steaming.Core.Models;

namespace Steaming.Core.Services;

// Resolves which custom alert (if any) a redeemed reward should fire. NO fuzzy matching: an explicit
// assignment in the rewards list wins; otherwise a custom alert whose name is EXACTLY the reward title
// (case-insensitive) is used. No match → caller falls back to the generic RewardRedemption alert.
internal static class CustomAlertMatcher
{
    public static bool TryResolveRewardAlert(
        AppSettings settings,
        string rewardId,
        string rewardTitle,
        string platform,
        out string matchedName,
        out EventConfig matchedConfig)
    {
        matchedName = string.Empty;
        matchedConfig = null!;

        // 1) Explicit assignment from the rewards list (matched by platform reward id, else by title).
        var reward = settings.Rewards?.FirstOrDefault(r =>
            string.Equals(r.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
            ((!string.IsNullOrEmpty(rewardId) &&
              string.Equals(r.Id, rewardId, StringComparison.OrdinalIgnoreCase)) ||
             (!string.IsNullOrWhiteSpace(rewardTitle) &&
              string.Equals(r.Title?.Trim(), rewardTitle.Trim(), StringComparison.OrdinalIgnoreCase))));

        if (reward != null &&
            !string.IsNullOrWhiteSpace(reward.AssignedAlert) &&
            settings.CustomAlerts.TryGetValue(reward.AssignedAlert!, out var assigned) &&
            assigned.Enabled)
        {
            matchedName = reward.AssignedAlert!;
            matchedConfig = assigned;
            return true;
        }

        // 2) Exact name match: a custom alert named exactly like the reward title.
        if (!string.IsNullOrWhiteSpace(rewardTitle))
        {
            foreach (var (name, cfg) in settings.CustomAlerts)
            {
                if (cfg.Enabled &&
                    string.Equals(name?.Trim(), rewardTitle.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    matchedName = name!;
                    matchedConfig = cfg;
                    return true;
                }
            }
        }

        return false;
    }
}
