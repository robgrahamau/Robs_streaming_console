namespace Steaming.Application.ViewModels;

// One channel-point reward row on the Overlays page. AssignedAlert links it to a custom alert that
// fires when the reward is redeemed ("(none)" = fall back to the generic RewardRedemption alert).
public class RewardItem : ViewModelBase
{
    public string Id { get; set; } = "";

    private string _platform = "";
    public string Platform { get => _platform; set { Set(ref _platform, value); Notify(nameof(Display)); } }

    private string _title = "";
    public string Title { get => _title; set { Set(ref _title, value); Notify(nameof(Display)); } }

    private int _cost;
    public int Cost { get => _cost; set { Set(ref _cost, value); Notify(nameof(Display)); } }

    private bool _enabled = true;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    // Custom alert name this reward fires; RewardsViewModel maps "(none)" ↔ null on save.
    private string _assignedAlert = "(none)";
    public string AssignedAlert { get => _assignedAlert; set => Set(ref _assignedAlert, value); }

    public string Display => $"{Platform} · {Title}" + (Cost > 0 ? $" ({Cost})" : "");
}
