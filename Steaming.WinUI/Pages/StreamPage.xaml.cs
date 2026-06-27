using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Steaming.Application.ViewModels;

namespace Steaming.WinUI.Pages;

public sealed partial class StreamPage : Page
{
    private MainViewModel? _vm;
    private bool _initializing;

    public StreamPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _vm = e.Parameter as MainViewModel;
        if (_vm == null) return;

        GameResults.ItemsSource = _vm.GameResults;
        RefreshLabels();

        _initializing = true;
        WarnTwitchCheck.IsChecked = _vm.WarnOnUnhealthyTwitch;
        WarnKickCheck.IsChecked   = _vm.WarnOnUnhealthyKick;
        _initializing = false;
        RefreshHealth();

        _ = _vm.RefreshChannelInfoAsync();
        _vm.PropertyChanged += OnVmChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.TwitchStreamTitle) or
            nameof(MainViewModel.KickStreamTitle) or
            nameof(MainViewModel.TwitchStreamCurrentGame) or
            nameof(MainViewModel.KickStreamCurrentGame))
            DispatcherQueue.TryEnqueue(RefreshLabels);
        else if (e.PropertyName is nameof(MainViewModel.StreamHealthText) or
                 nameof(MainViewModel.HealthWarning) or nameof(MainViewModel.StreamHealthy))
            DispatcherQueue.TryEnqueue(RefreshHealth);
    }

    private void RefreshHealth()
    {
        if (_vm == null) return;
        HealthLabel.Text     = _vm.StreamHealthText;
        HealthWarnLabel.Text = _vm.HealthWarning;
    }

    private void WarnTwitch_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_initializing || _vm == null) return;
        _vm.SetWarnOnUnhealthyTwitch(WarnTwitchCheck.IsChecked == true);
    }

    private void WarnKick_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_initializing || _vm == null) return;
        _vm.SetWarnOnUnhealthyKick(WarnKickCheck.IsChecked == true);
    }

    private void RefreshLabels()
    {
        if (_vm == null) return;
        TwitchTitleLabel.Text = _vm.TwitchStreamTitle;
        KickTitleLabel.Text   = _vm.KickStreamTitle;
        CategoryLabel.Text    = _vm.TwitchStreamCurrentGame != "—" ? _vm.TwitchStreamCurrentGame : _vm.KickStreamCurrentGame;
    }

    private async void UpdateTitle_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_vm == null) return;
        var title = TitleBox.Text.Trim();
        if (string.IsNullOrEmpty(title)) return;

        TitleResult.Text = "Updating...";
        var result = await _vm.UpdateTitleAsync(title,
            TitleTwitchCheck.IsChecked == true,
            TitleKickCheck.IsChecked   == true);
        TitleResult.Text = FormatResult(result);
        RefreshLabels();
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            DoSearch();
    }

    private void Search_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => DoSearch();

    private void DoSearch()
    {
        if (_vm == null) return;
        var q = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(q)) return;
        SearchBtn.IsEnabled = false;
        GameResult.Text = "Searching...";
        _ = DoSearchAsync(q);
    }

    private async Task DoSearchAsync(string query)
    {
        if (_vm == null) return;
        try
        {
            await _vm.SearchGamesAsync(query,
                GameTwitchCheck.IsChecked == true,
                GameKickCheck.IsChecked   == true);
            GameResult.Text = _vm.GameResults.Count == 0 ? "No results." : "";
        }
        catch (Exception ex) { GameResult.Text = ex.Message; }
        finally { SearchBtn.IsEnabled = true; }
    }

    private async void ApplyGame_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_vm == null || GameResults.SelectedItem is not Steaming.Core.Services.GameSearchResult game) return;
        GameResult.Text = "Applying...";
        var result = await _vm.UpdateGameAsync(game,
            GameTwitchCheck.IsChecked == true,
            GameKickCheck.IsChecked   == true);
        GameResult.Text = FormatResult(result);
        RefreshLabels();
    }

    private static string FormatResult(Steaming.Core.Services.PlatformUpdateResult r)
    {
        if (!r.AnyRequested) return "No platform selected.";
        if (r.AllSucceeded)  return "Done.";
        var errors = new List<string>();
        if (r.TwitchRequested && !r.TwitchSucceeded) errors.Add($"Twitch: {r.TwitchError}");
        if (r.KickRequested   && !r.KickSucceeded)   errors.Add($"Kick: {r.KickError}");
        return string.Join(" | ", errors);
    }

    private async void RefreshMeta_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_vm == null) return;
        await _vm.RefreshChannelInfoAsync();
        RefreshLabels();
    }
}
