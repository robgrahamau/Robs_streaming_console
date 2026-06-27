namespace Steaming.Application.ViewModels;

public class ServiceStatusItem : ViewModelBase
{
    public required string Key  { get; init; }
    public required string Name { get; init; }

    private string _state = "Pending";
    public string State { get => _state; set => Set(ref _state, value); }

    private string _summary = "";
    public string Summary { get => _summary; set => Set(ref _summary, value); }

    private string _details = "";
    public string Details { get => _details; set => Set(ref _details, value); }

    private string _accent = "#FF8A8A8A";
    public string Accent { get => _accent; set => Set(ref _accent, value); }
}
