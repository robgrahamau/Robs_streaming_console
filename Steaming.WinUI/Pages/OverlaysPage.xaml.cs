using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingFrameDimension = System.Drawing.Imaging.FrameDimension;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingImage = System.Drawing.Image;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using NAudio.Wave;
using Steaming.Application.ViewModels;
using Steaming.Core.Models;
using Steaming.Core.Services;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace Steaming.WinUI.Pages;

public sealed partial class OverlaysPage : Page
{
    private MainViewModel? _vm;
    private int _selectedGoalIdx = -1;

    // ─── Preview state ────────────────────────────────────────────────────────
    private AlertEditorViewModel? _previewVm;
    private readonly Dictionary<string, FrameworkElement> _previewElemControls = new();
    private readonly Dictionary<string, TextRenderCacheEntry> _previewTextRenderCache = new();
    private readonly Dictionary<string, (WriteableBitmap[] Frames, double[] CumSec, double TotalSec, long Bytes)> _previewGifData = new();
    private readonly Dictionary<string, Windows.Media.Editing.MediaComposition> _previewVideoCompositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _previewVideoLastFrameTime = new();
    private readonly Dictionary<string, double> _previewVideoPendingTime = new();
    private readonly HashSet<string> _previewVideoDecoding = new();
    private readonly HashSet<string> _previewVideoAspectFitted = new();
    private readonly List<string> _gifCacheOrder = new();
    private long _previewGifTotalBytes;
    private const long GifCacheMaxBytes = 500L * 1024 * 1024;
    private bool _renderingHooked;

    private sealed class TextRenderCacheEntry
    {
        public bool IsDualPass;
        public TextTransitionType TransitionType;
        public Grid? SingleGrid;
        public Grid? FromGrid;
        public Grid? ToGrid;
        public string? SingleSignature;
        public string? FromSignature;
        public string? ToSignature;
    }

    // ─── Preview audio ────────────────────────────────────────────────────────
    private WaveOutEvent?    _previewWaveOut;
    private AudioFileReader? _previewAudioReader;
    private readonly List<DispatcherQueueTimer> _previewClipTimers = new();
    private readonly List<(WaveOutEvent Output, AudioFileReader Reader, AlertElement El)> _previewClipPlayers = new();

    public OverlaysPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not MainViewModel vm) return;
        _vm = vm;
        vm.RefreshGoals();
        vm.RefreshLabels();
        LabelList.ItemsSource = vm.Labels;
        LoadAllEvents();
        BuildGoalList();
        UniqueAlertsList.ItemsSource = vm.CustomAlerts;
        RefreshUniqueEmptyState();

        vm.LoadRewards();
        RewardsList.DataContext = vm;                 // lets the per-row ComboBox reach RewardAlertOptions
        RewardsList.ItemsSource = vm.Rewards;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // CHANNEL POINT REWARDS
    // ═══════════════════════════════════════════════════════════════════════

    private async void RefreshRewards_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null) return;
        RefreshRewardsBtn.IsEnabled = false;
        RewardsStatusLabel.Text = "Refreshing…";
        try
        {
            var status = await _vm.RefreshRewardsFromPlatformsAsync();
            RewardsList.ItemsSource = _vm.Rewards;     // collection was rebuilt by LoadRewards()
            RewardsStatusLabel.Text = status;
        }
        catch (Exception ex)
        {
            RewardsStatusLabel.Text = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            RefreshRewardsBtn.IsEnabled = true;
        }
    }

    private void RewardAlertAssign_Changed(object s, SelectionChangedEventArgs e)
        => _vm?.SaveRewards();

    // ═══════════════════════════════════════════════════════════════════════
    // UNIQUE ALERTS
    // ═══════════════════════════════════════════════════════════════════════

    private void RefreshUniqueEmptyState()
        => NoUniqueAlertsLabel.Visibility = (_vm?.CustomAlerts.Count ?? 0) == 0
            ? Visibility.Visible : Visibility.Collapsed;

    private async void AddUniqueAlert_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var input = new TextBox { PlaceholderText = "Alert name (e.g. drak)" };
        var dlg = new ContentDialog
        {
            Title = "New Unique Alert",
            Content = input,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var name = input.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_vm.AddCustomAlert(name) == null)
        {
            await new ContentDialog
            {
                Title = "Unique Alert",
                Content = $"An alert named \"{name}\" already exists.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            }.ShowAsync();
            return;
        }
        RefreshUniqueEmptyState();
    }

    private void SaveUnique_Click(object s, RoutedEventArgs e)
    {
        _vm?.SaveCustomAlerts();
    }

    private void PreviewUnique_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null || (s as FrameworkElement)?.DataContext is not CustomAlertItem item) return;
        AlertLayout layout = !string.IsNullOrEmpty(item.LayoutJson)
            ? AlertLayout.FromJson(item.LayoutJson) ?? AlertLayout.CreateDefault()
            : AlertLayout.CreateDefault();
        float duration = int.TryParse(item.DurationText, out var d) && d > 0 ? d : 5f;

        StopPreviewRendering();
        _previewVm = new AlertEditorViewModel(layout, duration,
            string.IsNullOrWhiteSpace(item.SoundFile) ? null : item.SoundFile, item.Name);

        PreviewTitle.Text = $"Previewing: {item.Name}";
        PreviewHint.Visibility = Visibility.Collapsed;
        ReplayBtn.IsEnabled = true;

        RebuildPreviewCanvas();
        _previewVm.StartPlayback();
        StartPreviewAudio(_previewVm);
        StartPreviewRendering();
    }

    private async void TestUnique_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null || (s as FrameworkElement)?.DataContext is not CustomAlertItem item) return;
        _vm.SaveCustomAlerts();
        await _vm.TriggerCustomAlertTestAsync(item);
    }

    private async void EditUnique_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null || (s as FrameworkElement)?.DataContext is not CustomAlertItem item) return;
        AlertLayout layout = !string.IsNullOrEmpty(item.LayoutJson)
            ? AlertLayout.FromJson(item.LayoutJson) ?? AlertLayout.CreateDefault()
            : AlertLayout.CreateDefault();
        float duration = int.TryParse(item.DurationText, out var d) && d > 0 ? d : 5f;
        var result = await AlertEditorWindow.OpenAsync(layout, duration,
            string.IsNullOrWhiteSpace(item.SoundFile) ? null : item.SoundFile, item.Name, _vm);
        if (result != null)
            _vm.SaveCustomAlertEditorResult(item, result.LayoutJson, result.SoundFile, result.Volume, result.Duration);
    }

    private void DeleteUnique_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null || (s as FrameworkElement)?.DataContext is not CustomAlertItem item) return;
        _vm.RemoveCustomAlert(item);
        RefreshUniqueEmptyState();
    }

    private async void BrowseUniqueSound_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is not CustomAlertItem item) return;
        var path = await PickSoundFileAsync();
        if (path != null) item.SoundFile = path;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        StopPreviewRendering();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ALERT EVENTS
    // ═══════════════════════════════════════════════════════════════════════

    private void LoadAllEvents()
    {
        if (_vm == null) return;
        LoadEvent("Follow",           FollowEnabled,    FollowText,    FollowDuration,    FollowVolume,    FollowSound,    FollowCustomTag);
        LoadEvent("Subscribe",        SubscribeEnabled, SubscribeText, SubscribeDuration, SubscribeVolume, SubscribeSound, SubscribeCustomTag);
        LoadEvent("GiftSubscribe",    GiftEnabled,      GiftText,      GiftDuration,      GiftVolume,      GiftSound,      GiftCustomTag);
        LoadEvent("Bits",             BitsEnabled,      BitsText,      BitsDuration,      BitsVolume,      BitsSound,      BitsCustomTag);
        LoadEvent("Raid",             RaidEnabled,      RaidText,      RaidDuration,      RaidVolume,      RaidSound,      RaidCustomTag);
        LoadEvent("RewardRedemption", RewardEnabled,    RewardText,    RewardDuration,    RewardVolume,    RewardSound,    RewardCustomTag);
    }

    private void LoadEvent(string key, CheckBox enabled, TextBox text, TextBox duration, TextBox volume, TextBox sound, TextBlock customTag)
    {
        var cfg = _vm?.GetEventConfig(key);
        if (cfg == null) return;
        enabled.IsChecked  = cfg.Enabled;
        text.Text          = cfg.Text;
        duration.Text      = cfg.Duration.ToString();
        volume.Text        = cfg.Volume.ToString("F1");
        sound.Text         = cfg.SoundFile ?? "";
        bool hasCustom     = !string.IsNullOrEmpty(cfg.LayoutJson);
        customTag.Visibility = hasCustom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveOneEvent(string key, CheckBox enabled, TextBox text, TextBox duration, TextBox volume, TextBox sound)
    {
        if (_vm == null) return;
        var cfg = new EventConfig
        {
            Enabled   = enabled.IsChecked == true,
            Text      = text.Text,
            Duration  = int.TryParse(duration.Text, out var d) ? Math.Clamp(d, 1, 60) : 5,
            Volume    = float.TryParse(volume.Text, out var v) ? Math.Clamp(v, 0f, 1f) : 1f,
            SoundFile = string.IsNullOrWhiteSpace(sound.Text) ? null : sound.Text,
        };
        _vm.SaveAllEventSettings(new Dictionary<string, EventConfig> { [key] = cfg });
    }

    private async Task OpenLayoutEditor(string key)
    {
        if (_vm == null) return;
        var cfg = _vm.GetEventConfig(key);
        AlertLayout layout = cfg?.LayoutJson != null
            ? AlertLayout.FromJson(cfg.LayoutJson) ?? AlertLayout.CreateDefault()
            : AlertLayout.CreateDefault();
        float duration = cfg?.Duration > 0 ? cfg.Duration : 5f;
        var result = await AlertEditorWindow.OpenAsync(layout, duration, cfg?.SoundFile, key, _vm);
        if (result != null)
        {
            _vm.SaveEventEditorResult(key, result.LayoutJson, result.SoundFile, result.Volume, result.Duration);
            LoadAllEvents();
        }
    }

    private async Task TriggerTest(string key)
    {
        if (_vm == null) return;
        await _vm.TriggerTestAlertAsync(key);
    }

    // ── Preview ──────────────────────────────────────────────────────────────
    private void PreviewFollow_Click(object s, RoutedEventArgs e)    => StartAlertPreview("Follow");
    private void PreviewSubscribe_Click(object s, RoutedEventArgs e) => StartAlertPreview("Subscribe");
    private void PreviewGift_Click(object s, RoutedEventArgs e)      => StartAlertPreview("GiftSubscribe");
    private void PreviewBits_Click(object s, RoutedEventArgs e)      => StartAlertPreview("Bits");
    private void PreviewRaid_Click(object s, RoutedEventArgs e)      => StartAlertPreview("Raid");
    private void PreviewReward_Click(object s, RoutedEventArgs e)    => StartAlertPreview("RewardRedemption");

    // ── Test ─────────────────────────────────────────────────────────────────
    private async void TestFollow_Click(object s, RoutedEventArgs e)    => await TriggerTest("Follow");
    private async void TestSubscribe_Click(object s, RoutedEventArgs e) => await TriggerTest("Subscribe");
    private async void TestGift_Click(object s, RoutedEventArgs e)      => await TriggerTest("GiftSubscribe");
    private async void TestBits_Click(object s, RoutedEventArgs e)      => await TriggerTest("Bits");
    private async void TestRaid_Click(object s, RoutedEventArgs e)      => await TriggerTest("Raid");
    private async void TestReward_Click(object s, RoutedEventArgs e)    => await TriggerTest("RewardRedemption");

    // ── Edit Layout ──────────────────────────────────────────────────────────
    private async void EditLayoutFollow_Click(object s, RoutedEventArgs e)    => await OpenLayoutEditor("Follow");
    private async void EditLayoutSubscribe_Click(object s, RoutedEventArgs e) => await OpenLayoutEditor("Subscribe");
    private async void EditLayoutGift_Click(object s, RoutedEventArgs e)      => await OpenLayoutEditor("GiftSubscribe");
    private async void EditLayoutBits_Click(object s, RoutedEventArgs e)      => await OpenLayoutEditor("Bits");
    private async void EditLayoutRaid_Click(object s, RoutedEventArgs e)      => await OpenLayoutEditor("Raid");
    private async void EditLayoutReward_Click(object s, RoutedEventArgs e)    => await OpenLayoutEditor("RewardRedemption");

    // Ticking an Enabled box saves immediately — requiring a separate 💾 click made the
    // tick silently vanish when leaving the tab. CheckBox.Click only fires on user input,
    // so programmatic IsChecked changes during LoadEvent don't re-save.
    private void EventEnabled_Click(object s, RoutedEventArgs e)
    {
        if      (ReferenceEquals(s, FollowEnabled))    SaveFollow_Click(s, e);
        else if (ReferenceEquals(s, SubscribeEnabled)) SaveSubscribe_Click(s, e);
        else if (ReferenceEquals(s, GiftEnabled))      SaveGift_Click(s, e);
        else if (ReferenceEquals(s, BitsEnabled))      SaveBits_Click(s, e);
        else if (ReferenceEquals(s, RaidEnabled))      SaveRaid_Click(s, e);
        else if (ReferenceEquals(s, RewardEnabled))    SaveReward_Click(s, e);
    }

    // ── Save ─────────────────────────────────────────────────────────────────
    private void SaveFollow_Click(object s, RoutedEventArgs e)
        => SaveOneEvent("Follow", FollowEnabled, FollowText, FollowDuration, FollowVolume, FollowSound);
    private void SaveSubscribe_Click(object s, RoutedEventArgs e)
        => SaveOneEvent("Subscribe", SubscribeEnabled, SubscribeText, SubscribeDuration, SubscribeVolume, SubscribeSound);
    private void SaveGift_Click(object s, RoutedEventArgs e)
        => SaveOneEvent("GiftSubscribe", GiftEnabled, GiftText, GiftDuration, GiftVolume, GiftSound);
    private void SaveBits_Click(object s, RoutedEventArgs e)
        => SaveOneEvent("Bits", BitsEnabled, BitsText, BitsDuration, BitsVolume, BitsSound);
    private void SaveRaid_Click(object s, RoutedEventArgs e)
        => SaveOneEvent("Raid", RaidEnabled, RaidText, RaidDuration, RaidVolume, RaidSound);
    private void SaveReward_Click(object s, RoutedEventArgs e)
        => SaveOneEvent("RewardRedemption", RewardEnabled, RewardText, RewardDuration, RewardVolume, RewardSound);

    // ── Browse sound ─────────────────────────────────────────────────────────
    private async void BrowseFollowSound_Click(object s, RoutedEventArgs e)
        => FollowSound.Text    = await PickSoundFileAsync() ?? FollowSound.Text;
    private async void BrowseSubscribeSound_Click(object s, RoutedEventArgs e)
        => SubscribeSound.Text = await PickSoundFileAsync() ?? SubscribeSound.Text;
    private async void BrowseGiftSound_Click(object s, RoutedEventArgs e)
        => GiftSound.Text      = await PickSoundFileAsync() ?? GiftSound.Text;
    private async void BrowseBitsSound_Click(object s, RoutedEventArgs e)
        => BitsSound.Text      = await PickSoundFileAsync() ?? BitsSound.Text;
    private async void BrowseRaidSound_Click(object s, RoutedEventArgs e)
        => RaidSound.Text      = await PickSoundFileAsync() ?? RaidSound.Text;
    private async void BrowseRewardSound_Click(object s, RoutedEventArgs e)
        => RewardSound.Text    = await PickSoundFileAsync() ?? RewardSound.Text;

    private async Task<string?> PickSoundFileAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".mp3");
        picker.FileTypeFilter.Add(".wav");
        picker.FileTypeFilter.Add(".ogg");
        InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GOALS
    // ═══════════════════════════════════════════════════════════════════════

    private void BuildGoalList()
    {
        GoalListPanel.Children.Clear();
        if (_vm == null) return;
        foreach (var row in _vm.Goals)
            GoalListPanel.Children.Add(BuildGoalExpander(row));
    }

    private Expander BuildGoalExpander(GoalRow row)
    {
        // Body controls
        var enabledChk  = new CheckBox { Content = "Enabled", IsChecked = row.Enabled };
        var titleBox    = new TextBox  { PlaceholderText = "Goal title", Text = row.Title };
        var targetBox   = new TextBox  { PlaceholderText = "Target",     Text = row.Target };
        var currentBox  = new TextBox  { PlaceholderText = "Current",    Text = row.CurrentStr };
        var linkPicker  = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var lt in new[] { "Manual", "Followers", "Subscribers", "Viewers", "DonationTotalBits" })
            linkPicker.Items.Add(lt);
        linkPicker.SelectedItem = row.LinkType;

        var saveBtn = new Button { Content = "Save", Style = Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] as Style, FontSize = 11 };
        saveBtn.Click += (_, _) =>
        {
            if (_vm == null) return;
            _vm.TrySetGoalEnabled(row.Index, enabledChk.IsChecked == true, out _);
            _vm.TrySetGoalTitle(row.Index, titleBox.Text);
            if (int.TryParse(targetBox.Text, out var tgt))  _vm.TrySetGoalTarget(row.Index, tgt);
            if (int.TryParse(currentBox.Text, out var cur)) _vm.TrySetGoalCurrent(row.Index, cur, out _);
            _vm.TrySetGoalLinkType(row.Index, linkPicker.SelectedItem?.ToString() ?? "Manual");
            _ = _vm.PushAllGoalsAsync();
            BuildGoalList();
        };

        var editBtn = new Button { Content = "✏ Edit Layout", FontSize = 11 };
        editBtn.Click += async (_, _) =>
        {
            if (_vm == null) return;
            var cfg = _vm.GetGoalConfig(row.Index);
            AlertLayout layout = cfg?.LayoutJson != null
                ? AlertLayout.FromJson(cfg.LayoutJson) ?? AlertLayout.CreateDefaultGoal(cfg.Title, cfg.Target)
                : AlertLayout.CreateDefaultGoal(cfg?.Title ?? "Goal", cfg?.Target ?? 100);
            var result = await AlertEditorWindow.OpenAsync(layout, 0f, null, null, _vm, $"Goal {row.Index}: {row.Name}");
            if (result != null)
            {
                _vm.SaveGoalLayout(row.Index, result.LayoutJson);
            }
        };

        var previewBtn = new Button { Content = "👁", FontSize = 11 };
        ToolTipService.SetToolTip(previewBtn, "Preview");
        previewBtn.Click += (_, _) => StartGoalPreview(row.Index);

        var pushBtn = new Button { Content = "Push", FontSize = 11 };
        pushBtn.Click += async (_, _) => { if (_vm != null) await _vm.PushAllGoalsAsync(); };

        var bodyPanel = new StackPanel { Spacing = 8, Margin = new Thickness(4, 6, 4, 6) };
        bodyPanel.Children.Add(enabledChk);
        bodyPanel.Children.Add(MakeLabeledRow("Title",   titleBox));
        bodyPanel.Children.Add(MakeLabeledRow("Target",  targetBox));
        bodyPanel.Children.Add(MakeLabeledRow("Current", currentBox));
        bodyPanel.Children.Add(MakeLabeledRow("Linked to", linkPicker));
        bodyPanel.Children.Add(saveBtn);

        var headerBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        headerBtns.Children.Add(previewBtn);
        headerBtns.Children.Add(editBtn);
        headerBtns.Children.Add(pushBtn);

        var headerGrid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nameTb = new TextBlock { Text = row.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(nameTb, 0);
        Grid.SetColumn(headerBtns, 1);
        headerGrid.Children.Add(nameTb);
        headerGrid.Children.Add(headerBtns);

        var exp = new Expander
        {
            Header = headerGrid,
            Content = bodyPanel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        exp.Expanding += (_, _) => _selectedGoalIdx = row.Index;
        return exp;
    }

    private static Grid MakeLabeledRow(string label, FrameworkElement control)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lbl = new TextBlock
        {
            Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(180, 210, 210, 210)),
        };
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(control, 1);
        g.Children.Add(lbl);
        g.Children.Add(control);
        return g;
    }

    private void AddGoal_Click(object s, RoutedEventArgs e)
    {
        _vm?.AddGoal();
        BuildGoalList();
    }

    private void RemoveGoal_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null || _selectedGoalIdx < 0) return;
        _vm.DeleteGoal(_selectedGoalIdx);
        _selectedGoalIdx = -1;
        BuildGoalList();
    }

    private async void PushAllGoals_Click(object s, RoutedEventArgs e)
    {
        if (_vm != null) await _vm.PushAllGoalsAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // LABELS
    // ═══════════════════════════════════════════════════════════════════════

    private async void TestLabel_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null || (s as FrameworkElement)?.DataContext is not LabelRow row) return;
        await _vm.TestLabelAsync(row.Index);
    }

    private async void ClearLabel_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null || (s as FrameworkElement)?.DataContext is not LabelRow row) return;
        await _vm.ClearLabelAsync(row.Index);
    }

    private async void EditLabelLayout_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null || (s as FrameworkElement)?.DataContext is not LabelRow row) return;
        var cfg = _vm.GetLabelConfig(row.Index);
        AlertLayout layout = cfg?.LayoutJson != null
            ? AlertLayout.FromJson(cfg.LayoutJson) ?? AlertLayout.CreateDefaultLabel()
            : AlertLayout.CreateDefaultLabel();
        var result = await AlertEditorWindow.OpenAsync(layout, 0f, null, null, _vm, $"Label {row.Index}: {row.Name}");
        if (result != null)
        {
            _vm.SaveLabelLayout(row.Index, result.LayoutJson);
            _vm.RefreshLabels();
        }
    }

    private async void PushAllLabels_Click(object s, RoutedEventArgs e)
    {
        if (_vm != null) await _vm.PushAllLabelsAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TEXT VARIABLE REFERENCE
    // ═══════════════════════════════════════════════════════════════════════
    // The tokens here match the live replacements done in OverlayDispatcher.BuildAlertText
    // (standard events) and the custom-alert path (BuildAlertText overload with {arg}).

    private static readonly (string Token, string Meaning)[] AlertTokens =
    {
        ("{user}",          "The viewer's name (the follower, subscriber, cheerer, raider, …)."),
        ("{platform}",      "Where the event came from: Twitch, Kick or YouTube."),
        ("{amount}",        "The number involved: bits cheered, raider count, or months subscribed."),
        ("{amountDisplay}", "Donation / Super Chat amount already formatted, e.g. \"$5.00\"."),
        ("{months}",        "Total months a viewer has been subscribed (Subscribe alert)."),
        ("{target}",        "The recipient of a gifted sub (Gift Subscribe alert)."),
        ("{reward}",        "The channel-point reward name (Reward Redemption alert)."),
        ("{input}",         "Text the viewer typed when redeeming a reward (Reward Redemption alert)."),
    };

    private static readonly (string Token, string Meaning)[] UniqueAlertTokens =
    {
        ("{user}",     "Who triggered the alert."),
        ("{arg}",      "The word(s) typed after the trigger (e.g. the target of a \"!hug\" command)."),
        ("{platform}", "Where it came from: Twitch, Kick or YouTube."),
        ("{amount}",   "The number involved, when the trigger carries one."),
        ("{input}",    "Any extra text supplied with the trigger."),
        ("{reward}",   "The reward name, when triggered by a channel-point reward."),
    };

    private async void ShowTokenHelp_Click(object s, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 4 };

        panel.Children.Add(new TextBlock
        {
            Text = "Type any of these inside an alert's text box. They are replaced with the real "
                 + "values when the alert fires (and with sample values in Preview / Test).",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = new SolidColorBrush(Color.FromArgb(200, 210, 210, 210)),
        });

        AddTokenSection(panel, "Standard alerts  (Follow, Subscribe, Gift, Bits, Raid, Reward)", AlertTokens);
        AddTokenSection(panel, "Unique alerts",  UniqueAlertTokens);

        var dlg = new ContentDialog
        {
            Title = "Alert text variables",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
        };
        await dlg.ShowAsync();
    }

    private static void AddTokenSection(StackPanel parent, string heading, (string Token, string Meaning)[] tokens)
    {
        parent.Children.Add(new TextBlock
        {
            Text = heading,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 4),
        });

        foreach (var (token, meaning) in tokens)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var tokenTb = new TextBlock
            {
                Text = token,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0xFF, 0x8C, 0x00)),
            };
            Grid.SetColumn(tokenTb, 0);

            var meaningTb = new TextBlock
            {
                Text = meaning,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(meaningTb, 1);

            row.Children.Add(tokenTb);
            row.Children.Add(meaningTb);
            parent.Children.Add(row);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PREVIEW — ALERTS
    // ═══════════════════════════════════════════════════════════════════════

    private void StartAlertPreview(string eventKey)
    {
        if (_vm == null) return;
        var cfg = _vm.GetEventConfig(eventKey);
        AlertLayout layout = cfg?.LayoutJson != null
            ? AlertLayout.FromJson(cfg.LayoutJson) ?? AlertLayout.CreateDefault()
            : AlertLayout.CreateDefault();
        float duration = cfg?.Duration > 0 ? cfg.Duration : 5f;

        StopPreviewRendering();
        _previewVm = new AlertEditorViewModel(layout, duration, cfg?.SoundFile, eventKey);

        string label = eventKey switch
        {
            "GiftSubscribe" => "Gift Subscribe",
            _ => eventKey,
        };
        PreviewTitle.Text = $"Previewing: {label}";
        PreviewHint.Visibility = Visibility.Collapsed;
        ReplayBtn.IsEnabled = true;

        RebuildPreviewCanvas();
        _previewVm.StartPlayback();
        StartPreviewAudio(_previewVm);
        StartPreviewRendering();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PREVIEW — GOALS / LABELS (static — no animation, duration = 0)
    // ═══════════════════════════════════════════════════════════════════════

    private void StartGoalPreview(int idx)
    {
        if (_vm == null) return;
        var cfg = _vm.GetGoalConfig(idx);
        AlertLayout layout = cfg?.LayoutJson != null
            ? AlertLayout.FromJson(cfg.LayoutJson) ?? AlertLayout.CreateDefaultGoal(cfg.Title, cfg.Target)
            : AlertLayout.CreateDefaultGoal(cfg?.Title ?? "Goal", cfg?.Target ?? 100);

        StopPreviewRendering();
        _previewVm = new AlertEditorViewModel(layout, 0f, null, null);

        PreviewTitle.Text = $"Previewing: Goal {idx}";
        PreviewHint.Visibility = Visibility.Collapsed;
        ReplayBtn.IsEnabled = false;

        RebuildPreviewCanvas();
        UpdatePreviewCanvas();
    }

    private void PreviewLabel_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null || (s as FrameworkElement)?.DataContext is not LabelRow row) return;
        StartLabelPreview(row.Index);
    }

    private void StartLabelPreview(int idx)
    {
        if (_vm == null) return;
        var cfg = _vm.GetLabelConfig(idx);
        AlertLayout layout = cfg?.LayoutJson != null
            ? AlertLayout.FromJson(cfg.LayoutJson) ?? AlertLayout.CreateDefaultLabel()
            : AlertLayout.CreateDefaultLabel();

        StopPreviewRendering();
        _previewVm = new AlertEditorViewModel(layout, 0f, null, null);

        PreviewTitle.Text = $"Previewing: Label {idx}";
        PreviewHint.Visibility = Visibility.Collapsed;
        ReplayBtn.IsEnabled = false;

        RebuildPreviewCanvas();
        UpdatePreviewCanvas();
    }

    private void Replay_Click(object sender, RoutedEventArgs e)
    {
        if (_previewVm == null) return;
        _previewVm.StopPlayback();
        StopPreviewAudio();
        _previewVm.PreviewTime = 0f;
        // Rebuild exactly like the first play. Without this, replay reuses the text-render cache (and
        // gif/video frame state) left over from the previous run, so the second playback starts from
        // stale cached grids and does extra teardown/rebuild work the cold first play never did — which
        // shows up as stutter on replay. Replay must be identical to first play.
        RebuildPreviewCanvas();
        _previewVm.StartPlayback();
        StartPreviewAudio(_previewVm);
        StartPreviewRendering();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PREVIEW — CANVAS RENDERING
    // ═══════════════════════════════════════════════════════════════════════

    private void RebuildPreviewCanvas()
    {
        if (_previewVm == null) return;
        PreviewCanvas.Children.Clear();
        _previewElemControls.Clear();
        _previewTextRenderCache.Clear();
        _previewGifData.Clear();
        _gifCacheOrder.Clear();
        _previewGifTotalBytes = 0;
        ResetPreviewVideoFrameState();

        PreviewCanvas.Width  = _previewVm.Layout.Width;
        PreviewCanvas.Height = _previewVm.Layout.Height;

        foreach (var el in _previewVm.Layout.Elements.OrderBy(e => e.ZOrder))
        {
            var ctrl = CreatePreviewElementControl(el);
            if (ctrl == null) continue;
            ctrl.Tag = el.Id;
            _previewElemControls[el.Id] = ctrl;
            Canvas.SetLeft(ctrl, el.X);
            Canvas.SetTop(ctrl, el.Y);
            Canvas.SetZIndex(ctrl, el.ZOrder);
            PreviewCanvas.Children.Add(ctrl);
        }

        UpdatePreviewCanvas();
    }

    private FrameworkElement? CreatePreviewElementControl(AlertElement el)
    {
        switch (el.Type)
        {
            case AlertElementType.Rect:
            case AlertElementType.GoalBar:
            {
                var border = new Border { Width = Math.Abs(el.Width), Height = Math.Abs(el.Height), CornerRadius = new CornerRadius(Math.Min(el.CornerRadius, 999)) };
                if (!string.IsNullOrWhiteSpace(el.FillColor) && TryParseColor(el.FillColor, out var c))
                    border.Background = new SolidColorBrush(c);
                else
                    border.Background = new SolidColorBrush(Color.FromArgb(200, 33, 150, 243));
                return border;
            }
            case AlertElementType.Text:
            {
                var canvas = new Canvas { Width = Math.Abs(el.Width), Height = Math.Abs(el.Height),
                    Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, Math.Abs(el.Width), Math.Abs(el.Height)) } };
                var grid = new Grid { Width = Math.Abs(el.Width), Height = Math.Abs(el.Height) };
                BuildTextGrid(grid, el);
                canvas.Children.Add(grid);
                return canvas;
            }
            case AlertElementType.Image:
            {
                var img = new Microsoft.UI.Xaml.Controls.Image { Width = Math.Abs(el.Width), Height = Math.Abs(el.Height), Stretch = Stretch.Fill };
                if (!string.IsNullOrWhiteSpace(el.FilePath) && File.Exists(el.FilePath))
                    try { img.Source = new BitmapImage(new Uri(el.FilePath)); } catch { }
                return img;
            }
            case AlertElementType.Gif:
            {
                var img = new Microsoft.UI.Xaml.Controls.Image { Width = Math.Abs(el.Width), Height = Math.Abs(el.Height), Stretch = Stretch.Fill };
                if (!string.IsNullOrWhiteSpace(el.FilePath) && File.Exists(el.FilePath))
                {
                    try
                    {
                        var gif = LoadGifFrames(el.FilePath);
                        if (gif.Frames.Length > 0) { CacheGif(el.Id, gif); img.Source = gif.Frames[0]; }
                    }
                    catch { }
                }
                return img;
            }
            case AlertElementType.Video:
            {
                var img = new Microsoft.UI.Xaml.Controls.Image { Width = Math.Abs(el.Width), Height = Math.Abs(el.Height), Stretch = Stretch.Fill };
                if (!string.IsNullOrWhiteSpace(el.FilePath) && File.Exists(el.FilePath))
                {
                    float start = el.Keyframes.Count > 0 ? el.Keyframes.Min(k => k.Time) : 0f;
                    RequestPreviewVideoFrame(el.Id, el.FilePath, Math.Max(0.0, _previewVm?.PreviewTime - start ?? 0.0));
                }
                return img;
            }
            case AlertElementType.Audio:
                return null;
        }
        return null;
    }

    private void BuildTextGrid(Grid grid, AlertElement el,
        IList<Steaming.Core.Models.TextSpan>? evalSpans = null,
        AlertEditorViewModel.AnimState? st = null)
    {
        if (_previewVm == null) return;
        grid.Children.Clear();

        int evalVA = st?.vertAlign ?? el.VertAlign;
        int evalHA = st?.align     ?? (int)el.Align;
        var vertAlign = evalVA == 0 ? VerticalAlignment.Top
            : evalVA == 2 ? VerticalAlignment.Bottom
            : VerticalAlignment.Center;
        var textAlign = evalHA == 1 ? TextAlignment.Center
            : evalHA == 2 ? TextAlignment.Right
            : TextAlignment.Left;

        var spans = evalSpans ?? AlertEditorViewModel.EffectiveSpans(el);

        if (el.Shadow)
        {
            int ox = st?.shadowOffX ?? (int)Math.Round(Math.Cos(el.ShadowAngle * Math.PI / 180.0) * el.ShadowDistance);
            int oy = st?.shadowOffY ?? (int)Math.Round(Math.Sin(el.ShadowAngle * Math.PI / 180.0) * el.ShadowDistance);
            var shadow = MakeTextBlock(spans, vertAlign, textAlign, ParseBrush(el.ShadowColor, Colors.Black));
            shadow.Margin = new Thickness(ox, oy, -ox, -oy);
            grid.Children.Add(shadow);
        }
        if (el.Outline && el.OutlineWidth > 0)
        {
            var outlineBrush = ParseBrush(el.OutlineColor, Colors.Black);
            int ow = Math.Min(el.OutlineWidth, 3);
            for (int dy = -ow; dy <= ow; dy++)
                for (int dx = -ow; dx <= ow; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var o = MakeTextBlock(spans, vertAlign, textAlign, outlineBrush);
                    o.Margin = new Thickness(dx, dy, -dx, -dy);
                    grid.Children.Add(o);
                }
        }
        grid.Children.Add(MakeTextBlock(spans, vertAlign, textAlign, null));
    }

    private void UpdateTextTransitionCanvas(Canvas canvas, AlertElement el, float t, AlertEditorViewModel.AnimState? st)
    {
        if (_previewVm == null) return;
        double w = AnimatedTextWidth(el, st);
        if (!_previewTextRenderCache.TryGetValue(el.Id, out var cache))
        {
            cache = new TextRenderCacheEntry();
            _previewTextRenderCache[el.Id] = cache;
        }

        var trans = _previewVm.EvalTextTransitionState(el, t);
        if (!trans.InTransition)
        {
            var spans = _previewVm.EvalSpansAt(el, t);
            var signature = BuildTextRenderSignature(el, spans, st);

            if (cache.IsDualPass)
            {
                canvas.Children.Clear();
                cache.IsDualPass = false;
                cache.SingleGrid = null;
                cache.FromGrid = null;
                cache.ToGrid = null;
                cache.FromSignature = null;
                cache.ToSignature = null;
            }

            if (cache.SingleGrid == null)
            {
                cache.SingleGrid = MakeSpanGrid(el, spans, st);
                cache.SingleSignature = signature;
                canvas.Children.Clear();
                canvas.Children.Add(cache.SingleGrid);
            }
            else
            {
                SyncTextGridSize(cache.SingleGrid, el, st);
                if (!string.Equals(cache.SingleSignature, signature, StringComparison.Ordinal))
                {
                    BuildTextGrid(cache.SingleGrid, el, spans, st);
                    cache.SingleSignature = signature;
                }
            }

            if (canvas.Children.Count != 1 || !ReferenceEquals(canvas.Children[0], cache.SingleGrid))
            {
                canvas.Children.Clear();
                canvas.Children.Add(cache.SingleGrid);
            }

            return;
        }

        var fromSignature = BuildTextRenderSignature(el, trans.FromSpans, st);
        var toSignature = BuildTextRenderSignature(el, trans.ToSpans, st);

        if (!cache.IsDualPass || cache.TransitionType != trans.Type || cache.FromGrid == null || cache.ToGrid == null)
        {
            cache.IsDualPass = true;
            cache.TransitionType = trans.Type;
            cache.SingleGrid = null;
            cache.SingleSignature = null;
            cache.FromGrid = MakeSpanGrid(el, trans.FromSpans, st);
            cache.ToGrid = MakeSpanGrid(el, trans.ToSpans, st);
            cache.FromSignature = fromSignature;
            cache.ToSignature = toSignature;
            canvas.Children.Clear();
            canvas.Children.Add(cache.FromGrid);
            canvas.Children.Add(cache.ToGrid);
        }
        else
        {
            SyncTextGridSize(cache.FromGrid, el, st);
            SyncTextGridSize(cache.ToGrid, el, st);

            if (!string.Equals(cache.FromSignature, fromSignature, StringComparison.Ordinal))
            {
                BuildTextGrid(cache.FromGrid, el, trans.FromSpans, st);
                cache.FromSignature = fromSignature;
            }

            if (!string.Equals(cache.ToSignature, toSignature, StringComparison.Ordinal))
            {
                BuildTextGrid(cache.ToGrid, el, trans.ToSpans, st);
                cache.ToSignature = toSignature;
            }
        }

        var fromGrid = cache.FromGrid;
        var toGrid = cache.ToGrid;
        float frac = trans.Frac;
        canvas.Children.Clear();

        switch (trans.Type)
        {
            case TextTransitionType.Fade:
            case TextTransitionType.Morph:
                fromGrid.Opacity = 1.0 - frac;
                toGrid.Opacity   = frac;
                canvas.Children.Add(fromGrid);
                canvas.Children.Add(toGrid);
                break;

            case TextTransitionType.SlideLeft:
                fromGrid.RenderTransform = new TranslateTransform { X = -frac * w };
                toGrid.RenderTransform   = new TranslateTransform { X = (1 - frac) * w };
                canvas.Children.Add(fromGrid);
                canvas.Children.Add(toGrid);
                break;

            case TextTransitionType.SlideRight:
                fromGrid.RenderTransform = new TranslateTransform { X = frac * w };
                toGrid.RenderTransform   = new TranslateTransform { X = -(1 - frac) * w };
                canvas.Children.Add(fromGrid);
                canvas.Children.Add(toGrid);
                break;

            default:
                canvas.Children.Add(toGrid);
                break;
        }
    }

    private static void SyncTextGridSize(Grid grid, AlertElement el, AlertEditorViewModel.AnimState? st)
    {
        grid.Width = AnimatedTextWidth(el, st);
        grid.Height = AnimatedTextHeight(el, st);
    }

    private static double AnimatedTextWidth(AlertElement el, AlertEditorViewModel.AnimState? st)
        => Math.Max(1, Math.Abs((st?.w ?? el.Width) * (st?.scaleX ?? 1f)));

    private static double AnimatedTextHeight(AlertElement el, AlertEditorViewModel.AnimState? st)
        => Math.Max(1, Math.Abs((st?.h ?? el.Height) * (st?.scaleY ?? 1f)));

    private string BuildTextRenderSignature(
        AlertElement el,
        IList<Steaming.Core.Models.TextSpan> spans,
        AlertEditorViewModel.AnimState? st)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(st?.vertAlign ?? el.VertAlign).Append('|');
        sb.Append(st?.align ?? (int)el.Align).Append('|');
        sb.Append(st?.shadowOffX ?? int.MinValue).Append('|');
        sb.Append(st?.shadowOffY ?? int.MinValue).Append('|');
        sb.Append(spans.Count).Append('|');
        foreach (var span in spans)
        {
            sb.Append(span.Text).Append('\u001f');
            sb.Append(span.Color).Append('\u001f');
            sb.Append(span.FontFamily).Append('\u001f');
            sb.Append(span.FontSize).Append('\u001f');
            sb.Append(span.Bold ? '1' : '0').Append(span.Italic ? '1' : '0').Append('|');
        }

        return sb.ToString();
    }

    private Grid MakeSpanGrid(
        AlertElement el,
        IList<Steaming.Core.Models.TextSpan> spans,
        AlertEditorViewModel.AnimState? st)
    {
        var grid = new Grid
        {
            Width = AnimatedTextWidth(el, st),
            Height = AnimatedTextHeight(el, st),
            IsHitTestVisible = false,
        };
        BuildTextGrid(grid, el, spans, st);
        return grid;
    }

    private TextBlock MakeTextBlock(IList<Steaming.Core.Models.TextSpan> spans, VerticalAlignment vertAlign, TextAlignment textAlign, Brush? forceBrush)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = textAlign,
            VerticalAlignment = vertAlign,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        foreach (var span in spans)
        {
            string text = span.Text
                .Replace("{user}", _previewVm?.PreviewUser ?? "TestUser")
                .Replace("{message}", _previewVm?.PreviewMessage ?? "Test message")
                .Replace("{amount}", _previewVm?.PreviewAmount ?? "100")
                .Replace("{months}", _previewVm?.PreviewAmount ?? "6")
                .Replace("{target}", _previewVm?.PreviewTarget ?? "SomeViewer")
                .Replace("{reward}", _previewVm?.PreviewReward ?? "Hydrate")
                .Replace("{input}", _previewVm?.PreviewInput ?? "Big sip");
            tb.Inlines.Add(new Run
            {
                Text       = text,
                Foreground = forceBrush ?? ParseBrush(span.Color, Colors.White),
                FontFamily = new FontFamily(span.FontFamily ?? "Segoe UI"),
                FontSize   = Math.Max(8, span.FontSize),
                FontWeight = span.Bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                FontStyle  = span.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            });
        }
        return tb;
    }

    private void UpdatePreviewCanvas()
    {
        if (_previewVm == null) return;
        float t = _previewVm.PreviewTime;

        foreach (var el in _previewVm.Layout.Elements)
        {
            if (!_previewElemControls.TryGetValue(el.Id, out var ctrl)) continue;

            if (el.Keyframes.Count > 0)
            {
                float startTime = el.Keyframes.Min(k => k.Time);
                float endTime   = el.Keyframes.Max(k => k.Time);
                if (t < startTime || t > endTime) { ctrl.Visibility = Visibility.Collapsed; continue; }
            }

            ctrl.Visibility = Visibility.Visible;
            var st = _previewVm.EvalAnimated(el, t);
            double effW = st.w * st.scaleX;
            double effH = st.h * st.scaleY;
            double absW = Math.Max(1, Math.Abs(effW));
            double absH = Math.Max(1, Math.Abs(effH));
            Canvas.SetLeft(ctrl, st.x);
            Canvas.SetTop(ctrl, st.y);
            ctrl.Width   = absW;
            ctrl.Height  = absH;
            ctrl.Opacity = Math.Clamp(st.opacity, 0, 1);
            if (ctrl is Canvas clipCanvas && clipCanvas.Clip is RectangleGeometry rg)
                rg.Rect = new Windows.Foundation.Rect(0, 0, absW, absH);

            // Negative width/height = mirror in place (matches the editor + plugin).
            var tg = new TransformGroup();
            tg.Children.Add(new ScaleTransform
            {
                ScaleX = effW < 0 ? -1 : 1,
                ScaleY = effH < 0 ? -1 : 1,
                CenterX = absW / 2,
                CenterY = absH / 2,
            });
            tg.Children.Add(new RotateTransform { Angle = st.rotation, CenterX = absW / 2, CenterY = absH / 2 });
            ctrl.RenderTransform = tg;

            if (el.Type == AlertElementType.Rect || el.Type == AlertElementType.GoalBar)
            {
                if (ctrl is Border b)
                {
                    if (st.fillColor != null && TryParseColor(st.fillColor, out var fc))
                        b.Background = new SolidColorBrush(fc);
                    b.CornerRadius = new CornerRadius(Math.Min(st.cornerRadius, 999));
                }
            }

            if (el.Type == AlertElementType.Text && ctrl is Canvas textCanvas)
                UpdateTextTransitionCanvas(textCanvas, el, t, st);

            if (el.Type == AlertElementType.Gif
                && ctrl is Microsoft.UI.Xaml.Controls.Image gifImg
                && _previewGifData.TryGetValue(el.Id, out var gif)
                && gif.TotalSec > 0)
            {
                float elemStart = el.Keyframes.Count > 0 ? el.Keyframes.Min(k => k.Time) : 0f;
                double rel = Math.Max(0.0, t - elemStart) % gif.TotalSec;
                int fi = gif.Frames.Length - 1;
                for (int i = 0; i < gif.CumSec.Length - 1; i++)
                    if (rel < gif.CumSec[i + 1]) { fi = i; break; }
                gifImg.Source = gif.Frames[fi];
            }

            if (el.Type == AlertElementType.Video
                && ctrl is Microsoft.UI.Xaml.Controls.Image
                && !string.IsNullOrWhiteSpace(el.FilePath))
            {
                float elementStart = el.Keyframes.Count > 0 ? el.Keyframes.Min(k => k.Time) : 0f;
                double rel = Math.Max(0.0, t - elementStart);
                double dur = _previewVideoCompositions.TryGetValue(el.FilePath, out var comp) ? comp.Duration.TotalSeconds : 0.0;

                if (dur > 0 && rel >= dur &&
                    el.VideoEnd is VideoEndBehavior.EndHide or VideoEndBehavior.EndFade)
                {
                    if (el.VideoEnd == VideoEndBehavior.EndFade)
                    {
                        double k = 1.0 - (rel - dur) / 0.5;
                        if (k <= 0) { ctrl.Visibility = Visibility.Collapsed; continue; }
                        ctrl.Opacity = Math.Clamp(st.opacity * k, 0, 1);
                    }
                    else
                    {
                        ctrl.Visibility = Visibility.Collapsed;
                        continue;
                    }
                }

                double pos = rel;
                if (dur > 0) pos = VideoPlaybackTime(el.VideoEnd, rel, dur);
                RequestPreviewVideoFrame(el.Id, el.FilePath, pos);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PREVIEW — PLAYBACK
    // ═══════════════════════════════════════════════════════════════════════

    private static double VideoPlaybackTime(VideoEndBehavior endBehavior, double rel, double dur)
    {
        if (dur <= 0) return rel;
        return endBehavior switch
        {
            VideoEndBehavior.Loop => rel % dur,
            VideoEndBehavior.HoldFirst => rel >= dur ? 0.0 : rel,
            _ => Math.Min(rel, dur),
        };
    }

    private void StartPreviewRendering()
    {
        if (_renderingHooked) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
        _renderingHooked = true;
    }

    private void StopPreviewRendering()
    {
        if (!_renderingHooked) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
        _renderingHooked = false;
        _previewVm?.StopPlayback();
        StopPreviewAudio();
    }

    // Never throttles: decodes every distinct requested time at full rate, only skipping a re-decode
    // of the identical frame. Matches the editor's video path exactly so preview == editor playback.
    private void RequestPreviewVideoFrame(string elId, string path, double t)
    {
        if (_previewVideoDecoding.Contains(elId)) { _previewVideoPendingTime[elId] = t; return; }
        if (_previewVideoLastFrameTime.TryGetValue(elId, out var last) && last == t) return;
        _ = DecodePreviewVideoLoopAsync(elId, path, t);
    }

    private async Task DecodePreviewVideoLoopAsync(string elId, string path, double t)
    {
        _previewVideoDecoding.Add(elId);
        try
        {
            while (true)
            {
                _previewVideoLastFrameTime[elId] = t;
                var src = await DecodePreviewVideoFrameAsync(elId, path, t);
                if (src != null && _previewElemControls.TryGetValue(elId, out var ctrl)
                    && ctrl is Microsoft.UI.Xaml.Controls.Image img)
                    img.Source = src;

                if (_previewVideoPendingTime.TryGetValue(elId, out var pend))
                {
                    _previewVideoPendingTime.Remove(elId);
                    if (pend != t) { t = pend; continue; }
                }
                break;
            }
        }
        catch { }
        finally { _previewVideoDecoding.Remove(elId); }
    }

    private async Task<ImageSource?> DecodePreviewVideoFrameAsync(string elId, string path, double t)
    {
        var comp = await GetPreviewVideoCompositionAsync(path);
        if (comp == null) return null;
        double durSec = comp.Duration.TotalSeconds;
        double clamped = durSec > 0 ? Math.Clamp(t, 0, Math.Max(0, durSec - 0.05)) : Math.Max(0, t);
        var stream = await comp.GetThumbnailAsync(
            TimeSpan.FromSeconds(clamped), 0, 0, Windows.Media.Editing.VideoFramePrecision.NearestFrame);
        if (stream == null) return null;
        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(stream);

        if (_previewVideoAspectFitted.Add(elId) && bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
        {
            var target = _previewVm?.Layout.Elements.FirstOrDefault(x => x.Id == elId);
            if (target != null)
            {
                FitPreviewVideoAspect(target, bmp.PixelWidth, bmp.PixelHeight);
                if (_previewElemControls.TryGetValue(elId, out var ctrl))
                {
                    ctrl.Width = Math.Abs(target.Width);
                    ctrl.Height = Math.Abs(target.Height);
                }
            }
        }
        return bmp;
    }

    private async Task<Windows.Media.Editing.MediaComposition?> GetPreviewVideoCompositionAsync(string path)
    {
        if (_previewVideoCompositions.TryGetValue(path, out var comp)) return comp;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
            comp = new Windows.Media.Editing.MediaComposition();
            comp.Clips.Add(clip);
            _previewVideoCompositions[path] = comp;
            return comp;
        }
        catch { return null; }
    }

    private void ResetPreviewVideoFrameState()
    {
        _previewVideoLastFrameTime.Clear();
        _previewVideoPendingTime.Clear();
    }

    private static void FitPreviewVideoAspect(AlertElement el, int vw, int vh)
    {
        if (vw <= 0 || vh <= 0) return;
        if (el.Width == 0 || el.Height == 0) return;
        double scale = Math.Min(Math.Abs(el.Width) / vw, Math.Abs(el.Height) / vh);
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1;
        float signW = el.Width < 0 ? -1f : 1f;
        float signH = el.Height < 0 ? -1f : 1f;
        el.Width = signW * Math.Max(1, (float)Math.Round(vw * scale));
        el.Height = signH * Math.Max(1, (float)Math.Round(vh * scale));
    }

    private void OnRendering(object? sender, object e)
    {
        if (_previewVm == null || !_previewVm.IsPlaying) { StopPreviewRendering(); return; }
        bool ended = _previewVm.OnPlayTick();
        UpdatePreviewCanvas();
        PreviewTimeLabel.Text = $"{_previewVm.PreviewTime:F2}s";

        // Apply volume envelopes each frame to match OBS behaviour
        float t = _previewVm.PreviewTime;
        if (_previewAudioReader != null)
            _previewAudioReader.Volume = Math.Clamp(
                AlertEditorViewModel.EvalEnvelope(_previewVm.VolumeEnvelope, _previewVm.Layout.Volume, t), 0f, 1f);
        foreach (var clip in _previewClipPlayers)
            clip.Reader.Volume = Math.Clamp(
                AlertEditorViewModel.EvalEnvelope(
                    clip.El.VolumeEnvelope, (clip.El.VolumeL + clip.El.VolumeR) / 2f, t), 0f, 1f);

        if (ended) StopPreviewRendering();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PREVIEW — AUDIO
    // ═══════════════════════════════════════════════════════════════════════

    private void StartPreviewAudio(AlertEditorViewModel vm)
    {
        StopPreviewAudio();
        if (!string.IsNullOrWhiteSpace(vm.SoundFile) && File.Exists(vm.SoundFile))
        {
            try
            {
                _previewAudioReader = new AudioFileReader(vm.SoundFile);
                if (vm.Layout.Volume > 0) _previewAudioReader.Volume = Math.Clamp(vm.Layout.Volume, 0f, 1f);
                _previewWaveOut = new WaveOutEvent();
                _previewWaveOut.Init(_previewAudioReader);
                _previewWaveOut.Play();
            }
            catch { }
        }
        foreach (var el in vm.Layout.Elements.Where(e =>
            e.Type == AlertElementType.Audio &&
            !string.IsNullOrWhiteSpace(e.FilePath) && File.Exists(e.FilePath)))
        {
            var capturedEl = el;
            if (el.StartTime <= 0.05f)
            {
                StartPreviewClipPlayer(capturedEl);
            }
            else
            {
                var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
                timer.Interval = TimeSpan.FromSeconds(el.StartTime);
                timer.Tick += (_, _) => { timer.Stop(); _previewClipTimers.Remove(timer); StartPreviewClipPlayer(capturedEl); };
                _previewClipTimers.Add(timer);
                timer.Start();
            }
        }
    }

    private void StartPreviewClipPlayer(AlertElement el)
    {
        try
        {
            var reader = new AudioFileReader(el.FilePath);
            float vol = Math.Max(el.VolumeL, el.VolumeR);
            if (vol > 0) reader.Volume = Math.Clamp(vol, 0f, 1f);
            var output = new WaveOutEvent();
            output.Init(reader);
            output.Play();
            _previewClipPlayers.Add((output, reader, el));
        }
        catch { }
    }

    private void StopPreviewAudio()
    {
        foreach (var t in _previewClipTimers) t.Stop();
        _previewClipTimers.Clear();
        foreach (var (o, r, _) in _previewClipPlayers) { try { o.Stop(); o.Dispose(); r.Dispose(); } catch { } }
        _previewClipPlayers.Clear();
        _previewWaveOut?.Stop();
        _previewWaveOut?.Dispose();
        _previewWaveOut = null;
        _previewAudioReader?.Dispose();
        _previewAudioReader = null;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GIF LOADING
    // ═══════════════════════════════════════════════════════════════════════

    private void CacheGif(string id, (WriteableBitmap[] Frames, double[] CumSec, double TotalSec) gif)
    {
        long bytes = gif.Frames.Sum(f => (long)f.PixelWidth * f.PixelHeight * 4);
        if (_previewGifData.TryGetValue(id, out var existing))
        {
            _previewGifTotalBytes -= existing.Bytes;
            _gifCacheOrder.Remove(id);
        }
        while (_previewGifTotalBytes + bytes > GifCacheMaxBytes && _gifCacheOrder.Count > 0)
        {
            var oldKey = _gifCacheOrder[0];
            _gifCacheOrder.RemoveAt(0);
            if (_previewGifData.TryGetValue(oldKey, out var old))
            {
                _previewGifData.Remove(oldKey);
                _previewGifTotalBytes -= old.Bytes;
            }
        }
        _previewGifData[id] = (gif.Frames, gif.CumSec, gif.TotalSec, bytes);
        _gifCacheOrder.Add(id);
        _previewGifTotalBytes += bytes;
    }

    private static (WriteableBitmap[] Frames, double[] CumSec, double TotalSec) LoadGifFrames(string path)
    {
        using var image = DrawingImage.FromFile(path);
        var dim = new DrawingFrameDimension(image.FrameDimensionsList[0]);
        int fc = Math.Max(1, image.GetFrameCount(dim));
        var frames = new WriteableBitmap[fc];
        var cumSec = new double[fc];
        double total = 0;
        int[] delays = GetGifDelays(image, fc);
        for (int i = 0; i < fc; i++)
        {
            image.SelectActiveFrame(dim, i);
            using var bmp = new DrawingBitmap(image.Width, image.Height);
            using (var g = DrawingGraphics.FromImage(bmp)) g.DrawImage(image, 0, 0, image.Width, image.Height);
            frames[i] = BitmapToWriteable(bmp);
            cumSec[i]  = total;
            total += delays[i] / 1000.0;
        }
        return (frames, cumSec, total);
    }

    private static int[] GetGifDelays(DrawingImage image, int fc)
    {
        try
        {
            var item = image.GetPropertyItem(0x5100);
            if (item?.Value == null || item.Value.Length < fc * 4) return Enumerable.Repeat(100, fc).ToArray();
            var d = new int[fc];
            for (int i = 0; i < fc; i++) d[i] = Math.Max(10, BitConverter.ToInt32(item.Value, i * 4) * 10);
            return d;
        }
        catch { return Enumerable.Repeat(100, fc).ToArray(); }
    }

    private static WriteableBitmap BitmapToWriteable(DrawingBitmap src)
    {
        using var bmp = new DrawingBitmap(src.Width, src.Height, DrawingPixelFormat.Format32bppPArgb);
        using (var g = DrawingGraphics.FromImage(bmp)) g.DrawImage(src, 0, 0, src.Width, src.Height);
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppPArgb);
        try
        {
            byte[] px = new byte[Math.Abs(data.Stride) * data.Height];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            var wb = new WriteableBitmap(bmp.Width, bmp.Height);
            using var s = wb.PixelBuffer.AsStream();
            s.Write(px, 0, px.Length);
            return wb;
        }
        finally { bmp.UnlockBits(data); }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // COLOR HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private static bool TryParseColor(string hex, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.TrimStart('#');
        try
        {
            if (hex.Length == 6)
            {
                color = Color.FromArgb(255,
                    Convert.ToByte(hex[..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16));
                return true;
            }
            if (hex.Length == 8)
            {
                color = Color.FromArgb(
                    Convert.ToByte(hex[..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16),
                    Convert.ToByte(hex[6..8], 16));
                return true;
            }
        }
        catch { }
        return false;
    }

    private static Brush ParseBrush(string? hex, Color fallback)
        => TryParseColor(hex ?? "", out var c) ? new SolidColorBrush(c) : new SolidColorBrush(fallback);
}
