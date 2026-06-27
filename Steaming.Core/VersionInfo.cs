namespace Steaming.Core;

public static class VersionInfo
{
    public static string AppVersion { get; } =
        typeof(VersionInfo).Assembly.GetName().Version?.ToString(3) ?? "0.5.0";

    public static string DisplayVersion => $"v{AppVersion}";
}
