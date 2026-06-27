using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Steaming.Application.ViewModels;

namespace Steaming.WinUI.Pages;

public sealed partial class ObsConfigPage : Page
{
    private MainViewModel? _vm;

    private static readonly SolidColorBrush GreenBrush = new(Colors.LimeGreen);
    private static readonly SolidColorBrush RedBrush   = new(Colors.Red);
    private static readonly SolidColorBrush GrayBrush  = new(Colors.Gray);
    private bool _initializing;

    public ObsConfigPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not MainViewModel vm) return;
        _vm = vm;
        _vm.PropertyChanged += OnVmPropertyChanged;

        ObsAddressBox.Text     = vm.IntegrationConfig.ObsAddress;
        ObsPasswordBox.Password = vm.IntegrationConfig.ObsPassword;
        _initializing = true;
        AutoReconnectCheck.IsChecked = vm.ObsAutoReconnect;
        WarnTwitchCheck.IsChecked    = vm.WarnOnUnhealthyTwitch;
        WarnKickCheck.IsChecked      = vm.WarnOnUnhealthyKick;
        _initializing = false;
        RefreshStatus();
        _ = LoadScenesAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ObsConnected) or nameof(MainViewModel.ObsStatus)
                           or nameof(MainViewModel.ObsStreaming)
                           or nameof(MainViewModel.PipeConnected) or nameof(MainViewModel.PipeStatus))
            DispatcherQueue.TryEnqueue(RefreshStatus);
    }

    private void RefreshStatus()
    {
        if (_vm == null) return;
        PipeDot.Fill      = _vm.PipeConnected ? GreenBrush : GrayBrush;
        PipeStatusText.Text = _vm.PipeStatus;
        ObsStatusText.Text  = _vm.ObsStatus;

        if (!_vm.ObsConnected)
        {
            StreamDot.Fill        = GrayBrush;
            StreamStatusText.Text = "Streaming status: unknown (not connected)";
        }
        else
        {
            StreamDot.Fill        = _vm.ObsStreaming ? RedBrush : GrayBrush;
            StreamStatusText.Text = _vm.ObsStreaming ? "OBS is streaming (live)" : "OBS is not streaming";
        }
    }

    private void AutoReconnect_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _vm == null) return;
        _vm.SetObsAutoReconnect(AutoReconnectCheck.IsChecked == true);
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

    private async void ObsConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        try
        {
            await _vm.ConnectObsAsync(ObsAddressBox.Text.Trim(), ObsPasswordBox.Password);
            await LoadScenesAsync();
        }
        catch (Exception ex)
        {
            ObsStatusText.Text = "Connection failed";
            var dlg = new ContentDialog
            {
                Title = "OBS WebSocket",
                Content = $"Connection failed: {ex.Message}\n\nIs OBS running with the WebSocket server enabled (Tools → WebSocket Server Settings)?",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            await dlg.ShowAsync();
        }
    }

    private void ObsDisconnect_Click(object sender, RoutedEventArgs e) => _vm?.DisconnectObs();

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
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() => ObsStatusText.Text = $"Scene list failed: {ex.Message}");
        }
    }

    private async void SwitchScene_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || ScenePicker.SelectedItem is not string scene) return;
        try
        {
            await _vm.SwitchObsSceneAsync(scene);
        }
        catch (Exception ex)
        {
            ObsStatusText.Text = $"Scene switch failed: {ex.Message}";
        }
    }
}
