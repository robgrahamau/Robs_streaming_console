using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Steaming.Application.Services;
using Steaming.Application.ViewModels;
using Steaming.Core.Models;
using Steaming.Core.Services;

namespace Steaming.WinUI.Pages;

public sealed partial class DashboardPage : Page
{
    private MainViewModel?   _vm;
    private DispatcherTimer? _uptimeTicker;
    private bool             _initializing;

    // Avatar mini-preview
    private AvatarViewModel?  _avatarVm;
    private WriteableBitmap?  _avatarBitmap;
    private byte[]            _avatarBuf = new byte[AvatarRenderService.RenderWidth * AvatarRenderService.RenderHeight * 4];
    private DispatcherTimer?  _avatarTick;

    private static readonly SolidColorBrush GreenBrush = new(Colors.LimeGreen);
    private static readonly SolidColorBrush RedBrush   = new(Colors.OrangeRed);
    private static readonly SolidColorBrush GrayBrush  = new(Colors.Gray);

    public DashboardPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not MainViewModel vm) return;
        _vm = vm;

        ViewerList.ItemsSource = vm.ViewerItems;

        // Music mini-player shares the singleton MusicViewModel with the Music page.
        MusicWidget.DataContext = App.Services?.GetRequiredService<MusicViewModel>();

        ((INotifyCollectionChanged)vm.ChatMessages).CollectionChanged  += OnChatChanged;
        ((INotifyCollectionChanged)vm.ViewerItems).CollectionChanged   += OnViewersChanged;
        RebuildChatText();
        vm.PropertyChanged += OnVmPropertyChanged;

        StreamTitleInput.Text = vm.StreamTitle;

        _uptimeTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTicker.Tick += (_, _) => RefreshUptime();
        _uptimeTicker.Start();

        _initializing = true;
        WarnTwitchCheck.IsChecked = vm.WarnOnUnhealthyTwitch;
        WarnKickCheck.IsChecked   = vm.WarnOnUnhealthyKick;
        TwitchActiveToggle.IsOn   = vm.TwitchActive;
        KickActiveToggle.IsOn     = vm.KickActive;
        YouTubeActiveToggle.IsOn  = vm.YouTubeActive;
        _initializing = false;

        RefreshStats();
        RefreshPlatformDots();
        RefreshViewerCount();
        _ = LoadScenesAsync();

        // Avatar mini-preview
        _avatarVm = App.Services?.GetService<AvatarViewModel>();
        if (_avatarVm != null)
        {
            _avatarBitmap = new WriteableBitmap(AvatarRenderService.RenderWidth, AvatarRenderService.RenderHeight);
            DashAvatarImage.Source = _avatarBitmap;
            _avatarTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _avatarTick.Tick += OnAvatarTick;
            _avatarTick.Start();
            _avatarVm.PropertyChanged += OnAvatarVmChanged;
            RefreshAvatarStatus();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _uptimeTicker?.Stop();
        _uptimeTicker = null;
        _avatarTick?.Stop();
        _avatarTick = null;
        if (_avatarVm != null)
        {
            _avatarVm.PropertyChanged -= OnAvatarVmChanged;
            _avatarVm = null;
        }
        if (_vm == null) return;
        ((INotifyCollectionChanged)_vm.ChatMessages).CollectionChanged -= OnChatChanged;
        ((INotifyCollectionChanged)_vm.ViewerItems).CollectionChanged  -= OnViewersChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
    }

    // ── Avatar widget ─────────────────────────────────────────────────────────

    private void OnAvatarTick(object? sender, object e)
    {
        if (_avatarVm == null || _avatarBitmap == null) return;
        if (!_avatarVm.TryGetPreviewFrame(_avatarBuf)) return;
        using var stream = _avatarBitmap.PixelBuffer.AsStream();
        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(_avatarBuf, 0, _avatarBuf.Length);
        _avatarBitmap.Invalidate();

        // Update mic bar from render diagnostics
        // AvatarRenderService.LastAmplitude is set by the render thread
        // We access via AvatarViewModel's renderer field indirectly through the GetService reference
        // This is a read of a volatile float — safe without lock
        // (AvatarRenderService is a singleton; getting it directly from DI is fine here)
        var renderer = App.Services?.GetService<AvatarRenderService>();
        if (renderer != null) MicLevelBar.Value = renderer.LastAmplitude;
    }

    private void OnAvatarVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(RefreshAvatarStatus);
    }

    private void RefreshAvatarStatus()
    {
        if (_avatarVm == null) return;
        bool running = _avatarVm.IsRunning;
        AvatarDot.Fill        = running ? GreenBrush : GrayBrush;
        AvatarStatusLabel.Text = running ? "Running" : (string.IsNullOrEmpty(_avatarVm.ModelPath) ? "No model" : "Stopped");
        int exprCount = _avatarVm.DiagExprCount;
        AvatarExprLabel.Text  = exprCount > 0 ? $"{exprCount} expressions" : "";
    }

    private void OnChatChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(RebuildChatText);

    private void RebuildChatText()
    {
        if (_vm == null) return;
        ChatTextView.Text = string.Join("\n", _vm.ChatMessages.Select(m => m.FormattedText));
        // A TextBox in WinUI 3 has no ScrollToEnd(), and Select(end) only nudges the caret
        // into view when focused — it does NOT keep the view pinned to the bottom once the
        // content exceeds the viewport. Drive the inner ScrollViewer to the bottom directly.
        ScrollChatToBottom();
    }

    private void ScrollChatToBottom()
    {
        // The new text must lay out before ScrollableHeight is correct, so defer the scroll.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            var sv = _chatScroll ??= FindDescendant<ScrollViewer>(ChatTextView);
            if (sv == null) return;
            sv.UpdateLayout();
            sv.ChangeView(null, sv.ScrollableHeight, null, disableAnimation: true);
        });
    }

    private ScrollViewer? _chatScroll;

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var result = FindDescendant<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    private void OnViewersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(RefreshViewerCount);
    }

    private void RefreshViewerCount()
    {
        if (_vm == null) return;
        int count = _vm.ViewerItems.Count;
        ViewerListCount.Text = count > 0 ? $"({count})" : "";
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.ViewerCount):
                case nameof(MainViewModel.TwitchViewerCount):
                case nameof(MainViewModel.KickViewerCount):
                case nameof(MainViewModel.YouTubeViewerCount):
                case nameof(MainViewModel.FollowerCount):
                case nameof(MainViewModel.TwitchFollowerCount):
                case nameof(MainViewModel.KickFollowerCount):
                case nameof(MainViewModel.KickFollowerCountKnown):
                case nameof(MainViewModel.SubscriberCount):
                case nameof(MainViewModel.TwitchSubscriberCount):
                case nameof(MainViewModel.KickSubscriberCount):
                case nameof(MainViewModel.IsLive):
                case nameof(MainViewModel.LiveStatusText):
                case nameof(MainViewModel.StreamTitle):
                    RefreshStats();
                    if (_vm != null && string.IsNullOrWhiteSpace(StreamTitleInput.Text))
                        StreamTitleInput.Text = _vm.StreamTitle;
                    break;
                case nameof(MainViewModel.StreamStartedAt):
                    RefreshStats();
                    break;
                case nameof(MainViewModel.TwitchActive):
                case nameof(MainViewModel.KickActive):
                case nameof(MainViewModel.YouTubeActive):
                    RefreshStats();
                    RefreshPlatformDots();
                    break;
                case nameof(MainViewModel.TwitchConnected):
                case nameof(MainViewModel.KickConnected):
                case nameof(MainViewModel.ObsConnected):
                case nameof(MainViewModel.PipeConnected):
                    RefreshPlatformDots();
                    if (e.PropertyName == nameof(MainViewModel.ObsConnected) && _vm?.ObsConnected == true)
                        _ = LoadScenesAsync();
                    break;
            }
        });
    }

    private void RefreshStats()
    {
        if (_vm == null) return;
        ViewerCountLabel.Text   = _vm.ViewerCount.ToString();
        ViewerTwitchRun.Text = _vm.TwitchViewerCount.ToString();
        ViewerKickRun.Text   = _vm.KickViewerCount.ToString();
        ViewerYouTubeRun.Text = _vm.YouTubeViewerCount.ToString();
        FollowerCountLabel.Text = _vm.FollowerCount.ToString();
        FollowerTwitchRun.Text = _vm.TwitchFollowerCount.ToString();
        FollowerKickRun.Text   = _vm.KickFollowerCount.ToString();
        SubCountLabel.Text      = _vm.SubscriberCount.ToString();
        SubTwitchRun.Text = _vm.TwitchSubscriberCount.ToString();
        SubKickRun.Text   = _vm.KickSubscriberCount.ToString();
        StreamTitleLabel.Text   = string.IsNullOrWhiteSpace(_vm.StreamTitle) ? "—" : _vm.StreamTitle;

        // Grey a platform's breakdown numbers when it's switched off, so an inactive platform's
        // "0" reads as "not streaming here" rather than "zero viewers".
        var activeBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SystemControlForegroundBaseMediumBrush"];
        var twitchBrush  = _vm.TwitchActive  ? activeBrush : GrayBrush;
        var kickBrush    = _vm.KickActive    ? activeBrush : GrayBrush;
        var youtubeBrush = _vm.YouTubeActive ? activeBrush : GrayBrush;
        ViewerTwitchRun.Foreground  = FollowerTwitchRun.Foreground = SubTwitchRun.Foreground = twitchBrush;
        ViewerKickRun.Foreground    = FollowerKickRun.Foreground   = SubKickRun.Foreground   = kickBrush;
        ViewerYouTubeRun.Foreground = youtubeBrush;

        RefreshUptime();
        RefreshLiveIndicator();
    }

    private void RefreshUptime()
    {
        if (_vm == null) return;
        if (_vm.StreamStartedAt is { } start)
        {
            var elapsed = DateTimeOffset.UtcNow - start;
            UptimeLabel.Text = elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes:D2}m {elapsed.Seconds:D2}s"
                : $"{elapsed.Minutes:D2}m {elapsed.Seconds:D2}s";
        }
        else
        {
            UptimeLabel.Text = "—";
        }
    }

    private void RefreshLiveIndicator()
    {
        if (_vm == null) return;
        bool live      = _vm.IsLive;
        LiveDot.Fill   = live ? GreenBrush : GrayBrush;
        LiveLabel.Text = live ? $"LIVE  {_vm.LiveStatusText}" : "OFFLINE";
        LiveLabel.Foreground = live ? (Brush)GreenBrush
            : (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SystemControlForegroundBaseMediumBrush"];
    }

    private void RefreshPlatformDots()
    {
        if (_vm == null) return;
        // An inactive platform shows a grey dot regardless of its login/connection state.
        TwitchDot.Fill = !_vm.TwitchActive ? GrayBrush : _vm.TwitchConnected ? GreenBrush : RedBrush;
        KickDot.Fill   = !_vm.KickActive   ? GrayBrush : _vm.KickConnected   ? GreenBrush : RedBrush;
        YouTubeDot.Fill = !_vm.YouTubeActive ? GrayBrush : _vm.YouTubeConnected ? GreenBrush : RedBrush;
        ObsDot.Fill    = (_vm.ObsConnected || _vm.PipeConnected) ? GreenBrush : RedBrush;
    }

    private async Task LoadScenesAsync()
    {
        if (_vm == null || !_vm.ObsConnected) return;
        try
        {
            var (scenes, current) = await _vm.GetObsScenesAsync();
            DispatcherQueue.TryEnqueue(() =>
            {
                ScenePicker.ItemsSource = scenes;
                if (!string.IsNullOrEmpty(current) && scenes.Contains(current))
                    ScenePicker.SelectedItem = current;
            });
        }
        catch { /* OBS not connected */ }
    }

    private async void ScenePicker_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || ScenePicker.SelectedItem is not string scene) return;
        await _vm.SwitchObsSceneAsync(scene);
    }

    private async void UpdateTitle_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var title = StreamTitleInput.Text.Trim();
        if (string.IsNullOrEmpty(title)) return;
        UpdateTitleBtn.IsEnabled = false;
        UpdateTitleStatus.Visibility = Visibility.Collapsed;
        bool twitch = UpdateTwitchCheck.IsChecked == true;
        bool kick   = UpdateKickCheck.IsChecked   == true;
        var result  = await _vm.UpdateTitleAsync(title, twitch, kick);
        UpdateTitleBtn.IsEnabled = true;
        UpdateTitleStatus.Text = result.AllSucceeded ? "✓ Updated" : $"Failed: {result.TwitchError ?? result.KickError}";
        UpdateTitleStatus.Visibility = Visibility.Visible;
    }

    private async void TestAlert_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        await _vm.TriggerTestAlertAsync("Follow");
    }

    private void WarnTwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _vm == null) return;
        _vm.SetWarnOnUnhealthyTwitch(WarnTwitchCheck.IsChecked == true);
    }

    private void WarnKick_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _vm == null) return;
        _vm.SetWarnOnUnhealthyKick(WarnKickCheck.IsChecked == true);
    }

    private void PlatformActive_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _vm == null || sender is not ToggleSwitch ts) return;
        var platform = ts.Tag switch
        {
            "Twitch"  => Platform.Twitch,
            "Kick"    => Platform.Kick,
            "YouTube" => Platform.YouTube,
            _         => (Platform?)null,
        };
        if (platform is { } p)
        {
            _vm.SetPlatformActive(p, ts.IsOn);
            RefreshStats();
            RefreshPlatformDots();
        }
    }

    private async void SendChat_Click(object sender, RoutedEventArgs e) => await SendChatAsync();

    private async void ChatInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) await SendChatAsync();
    }

    private async Task SendChatAsync()
    {
        if (_vm == null || string.IsNullOrWhiteSpace(ChatInput.Text)) return;
        var text = ChatInput.Text.Trim();
        ChatInput.Text = "";
        var target = SendTargetPicker.SelectedIndex switch
        {
            1 => BotReplyTarget.Twitch,
            2 => BotReplyTarget.Kick,
            3 => BotReplyTarget.YouTube,
            _ => BotReplyTarget.Both,
        };
        await _vm.SendChatAsync(text, target);
    }
}
