namespace Steaming.Application.ViewModels;

public class GoalRow
{
    public int    Index      { get; set; }
    public string IndexLabel { get; set; } = "";
    public string Name       { get; set; } = "";
    public string Progress   { get; set; } = "";
    public bool   Enabled    { get; set; }
    public string Title      { get; set; } = "";
    public string Target     { get; set; } = "";
    public string CurrentStr { get; set; } = "";
    public string LinkType   { get; set; } = "Manual";
}
