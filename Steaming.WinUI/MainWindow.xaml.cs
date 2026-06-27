using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Steaming.Application.ViewModels;
using Steaming.Core;

namespace Steaming.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    private static readonly SolidColorBrush GreenBrush = new(Colors.LimeGreen);
    private static readonly SolidColorBrush GrayBrush  = new(Colors.Gray);
    private static readonly SolidColorBrush HealthGoodBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x2E, 0x7D, 0x46));
    private static readonly SolidColorBrush HealthBadBrush  = new(Windows.UI.Color.FromArgb(0xFF, 0xC0, 0x6A, 0x16));
    private static readonly SolidColorBrush HealthIdleBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x4A, 0x4A, 0x4A));
    private static readonly SolidColorBrush AmberBrush      = new(Colors.Orange);

    // Invisible host for headless WebView2 work (Kick chatroom-id lookup past Cloudflare).
    public Panel WebViewHost => HiddenWebHost;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        Title            = $"Rob's Streaming Console {VersionInfo.DisplayVersion}";
        VersionText.Text = VersionInfo.DisplayVersion;

        // Window/taskbar icon — WinUI 3 does not pick up an icon automatically
        try { AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico")); }
        catch { }

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.StreamMetadataMismatchDetected += OnStreamMetadataMismatch;
        _vm.AuthReconnectRequired += OnAuthReconnectRequired;
        RefreshStatusBar();

        // Navigate to Dashboard by default
        NavView.SelectedItem = NavDashboard;
        ContentFrame.Navigate(typeof(Pages.DashboardPage), _vm);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.IsLive):
                case nameof(MainViewModel.LiveStatusText):
                case nameof(MainViewModel.TwitchConnected):
                case nameof(MainViewModel.TwitchStatus):
                case nameof(MainViewModel.KickConnected):
                case nameof(MainViewModel.KickStatus):
                case nameof(MainViewModel.PipeConnected):
                case nameof(MainViewModel.PipeStatus):
                case nameof(MainViewModel.StreamHealthText):
                case nameof(MainViewModel.StreamHealthy):
                case nameof(MainViewModel.HealthWarning):
                case nameof(MainViewModel.ObsStreaming):
                    RefreshStatusBar();
                    break;
            }
        });
    }

    private void RefreshStatusBar()
    {
        bool live = _vm.IsLive;
        LiveDot.Fill          = live ? GreenBrush : GrayBrush;
        LiveStatusText.Text   = _vm.LiveStatusText;
        LiveStatusText.Foreground = live ? (Brush)GreenBrush
            : (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SystemControlForegroundBaseMediumBrush"];

        TwitchDot.Fill        = _vm.TwitchConnected ? GreenBrush : GrayBrush;
        TwitchStatusText.Text = _vm.TwitchStatus;

        KickDot.Fill          = _vm.KickConnected ? GreenBrush : GrayBrush;
        KickStatusText.Text   = _vm.KickStatus;

        PipeDot.Fill          = _vm.PipeConnected ? GreenBrush : GrayBrush;
        PipeStatusText.Text   = _vm.PipeStatus;

        // Stream health pill: green when streaming and all watched destinations healthy,
        // amber on a problem, grey when nothing is streaming.
        HealthPillText.Text = _vm.StreamHealthText;
        HealthPill.Background = !_vm.ObsStreaming ? HealthIdleBrush
                              : _vm.StreamHealthy ? HealthGoodBrush
                              : HealthBadBrush;
        HealthWarningText.Text = _vm.HealthWarning;
        HealthWarningText.Foreground = AmberBrush;
    }

    private bool _metadataDialogOpen;
    private bool _authReconnectDialogOpen;

    // On stream start the VM compares the live title/category to what you set; if they differ it
    // raises this so we can ask whether to re-apply (per the user's "ask if you want to" choice).
    private void OnStreamMetadataMismatch(MainViewModel.StreamMetadataMismatch m)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_metadataDialogOpen || Content?.XamlRoot == null) return;
            _metadataDialogOpen = true;
            try
            {
                var dlg = new ContentDialog
                {
                    Title             = "Stream info may not have applied",
                    Content           = m.Detail + "\n\nRe-apply the title/category you set?",
                    PrimaryButtonText = "Re-apply",
                    CloseButtonText   = "Ignore",
                    DefaultButton     = ContentDialogButton.Primary,
                    XamlRoot          = Content.XamlRoot,
                };
                if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                    await _vm.ReapplyStreamMetadataAsync(m.TitleMismatch, m.GameMismatch);
            }
            catch { }
            finally
            {
                _metadataDialogOpen = false;
                ShowPendingAuthReconnectPrompts();
            }
        });
    }

    public void ShowPendingAuthReconnectPrompts()
    {
        foreach (var prompt in _vm.GetPendingAuthReconnectPrompts())
            OnAuthReconnectRequired(prompt);
    }

    private void OnAuthReconnectRequired(MainViewModel.AuthReconnectPrompt prompt)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_authReconnectDialogOpen || _metadataDialogOpen || Content?.XamlRoot == null) return;
            _authReconnectDialogOpen = true;
            try
            {
                var dlg = new ContentDialog
                {
                    Title             = prompt.Title,
                    Content           = prompt.Message,
                    PrimaryButtonText = "Open Connections",
                    CloseButtonText   = "Close",
                    DefaultButton     = ContentDialogButton.Primary,
                    XamlRoot          = Content.XamlRoot,
                };
                _vm.MarkAuthReconnectPromptShown(prompt.Platform);
                if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                {
                    NavView.SelectedItem = NavConnections;
                    ContentFrame.Navigate(typeof(Pages.ConnectionsPage), _vm);
                }
            }
            catch { }
            finally
            {
                _authReconnectDialogOpen = false;
                if (_vm.GetPendingAuthReconnectPrompts().Count > 0)
                    ShowPendingAuthReconnectPrompts();
            }
        });
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not NavigationViewItem item) return;

        Type? pageType = item.Tag?.ToString() switch
        {
            "Dashboard"    => typeof(Pages.DashboardPage),
            "Stream"       => typeof(Pages.StreamPage),
            "Viewers"      => typeof(Pages.ViewersPage),
            "Activity"     => typeof(Pages.ActivityPage),
            "Overlays"     => typeof(Pages.OverlaysPage),
            "Chat"         => typeof(Pages.ChatPage),
            "Chatbot"      => typeof(Pages.ChatbotPage),
            "EmojiRain"    => typeof(Pages.EmojiRainPage),
            "Music"        => typeof(Pages.MusicPage),
            "Analytics"    => typeof(Pages.AnalyticsPage),
            "Avatar"       => typeof(Pages.AvatarPage),
            "Status"       => typeof(Pages.StatusPage),
            "Connections"  => typeof(Pages.ConnectionsPage),
            "ObsConfig"    => typeof(Pages.ObsConfigPage),
            "Settings"     => typeof(Pages.SettingsPage),
            "About"        => typeof(Pages.AboutPage),
            _ => null
        };

        if (pageType != null && ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType, _vm);
    }
}
