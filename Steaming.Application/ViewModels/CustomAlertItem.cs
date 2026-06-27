namespace Steaming.Application.ViewModels;

// One user-defined "Unique" alert row on the Alerts page. Mirrors the editable fields of an
// event alert; LayoutJson/ImageFile are set via the alert layout editor and preserved here.
public class CustomAlertItem : ViewModelBase
{
    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    private bool _enabled = true;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    private string _text = "{user}";
    public string Text { get => _text; set => Set(ref _text, value); }

    private string _durationText = "5";
    public string DurationText { get => _durationText; set => Set(ref _durationText, value); }

    private string _volumeText = "1.0";
    public string VolumeText { get => _volumeText; set => Set(ref _volumeText, value); }

    private string _soundFile = "";
    public string SoundFile { get => _soundFile; set => Set(ref _soundFile, value); }

    // Hidden — round-tripped through the layout editor, not edited inline.
    public string? LayoutJson { get; set; }
    public string? ImageFile  { get; set; }
}
