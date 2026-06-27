namespace Steaming.Application.ViewModels;

// One line in the in-app lyrics preview. Carries resolved style so the preview can mirror
// the OBS lyrics overlay (font/size/colour, active line bold + enlarged).
public sealed class LyricPreviewLine
{
    public string Text       { get; init; } = "";
    public double FontSize   { get; init; } = 30;
    public bool   IsActive   { get; init; }
    public string ColorHex   { get; init; } = "#FFFFFF";
    public string FontFamily { get; init; } = "Segoe UI";
}
