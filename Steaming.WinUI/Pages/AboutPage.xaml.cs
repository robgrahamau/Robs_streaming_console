using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Steaming.Application.ViewModels;
using Steaming.Core;

namespace Steaming.WinUI.Pages;

public sealed partial class AboutPage : Page
{
    // Static display content describing the third-party components shipped with the app and the
    // license each is used under. This is reference text for the view, not application state.
    private sealed record Acknowledgement(string Name, string License, string Note);

    private static readonly Acknowledgement[] Acknowledgements =
    [
        new("Windows App SDK / WinUI 3", "Microsoft license", "Microsoft — application UI framework"),
        new("Microsoft Edge WebView2", "Microsoft license", "Microsoft — embedded browser for auth and lookups"),
        new(".NET & Microsoft.Extensions", "MIT", "Microsoft — runtime, hosting, DI, logging, configuration"),
        new("CommunityToolkit.WinUI", "MIT", ".NET Foundation — additional WinUI controls"),
        new("WinUI.Dock", "MIT", "Dockable panel layout"),
        new("LiveCharts2 (LiveChartsCore)", "MIT", "Analytics charts"),
        new("SkiaSharp", "MIT", "Microsoft / Mono — 2D graphics used by charts"),
        new("NAudio", "MIT", "Mark Heath — audio playback for the music player"),
        new("TagLib#", "LGPL-2.1", "Audio file tag reading"),
        new("SharpGLTF", "MIT", "glTF model loading for the avatar"),
        new("ONNX Runtime", "MIT", "Microsoft — on-device ML inference"),
        new("Vortice.Windows", "MIT", "Direct3D 11 / D3DCompiler interop"),
        new("Microsoft.Data.Sqlite", "MIT", "Microsoft — SQLite access for analytics"),
        new("SQLite", "Public domain", "Embedded analytics database engine"),
        new("System.Security.Cryptography.ProtectedData", "MIT", "Microsoft — DPAPI token encryption"),
        new("TwitchLib", "MIT", "Twitch API, chat and EventSub client"),
        new("OBS Studio", "GPL-2.0", "Plugin API the native C++ plugin is built against"),
    ];

    public AboutPage()
    {
        InitializeComponent();
        VersionLine.Text = VersionInfo.DisplayVersion;
        LicenseList.ItemsSource = Acknowledgements;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is MainViewModel vm)
            VersionLine.Text = vm.AppVersion;
    }
}
