using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Steaming.Application.ViewModels;
using Steaming.Core.Models;
using Windows.UI;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Steaming.WinUI.Pages;

public sealed partial class MusicPage : Page
{
    private MusicViewModel? _vm;
    private bool _suppressDeviceCombo;
    private List<MusicTrack>? _dragItems;   // non-null only while a library drag is in flight

    public MusicPage()
    {
        InitializeComponent();
        // Slider marks pointer events handled internally, so XAML-attached PointerPressed/Released
        // fire unreliably (the seek would never register → lyrics stuck). Attach with handledEventsToo.
        SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(SeekSlider_PointerPressed), true);
        SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(SeekSlider_PointerReleased), true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(SeekSlider_PointerReleased), true);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _vm = App.Services?.GetRequiredService<MusicViewModel>();
        if (_vm == null) return;
        DataContext = _vm;

        SyncDeviceSelection();
        await _vm.EnsureLoadedAsync();
    }

    private void SyncDeviceSelection()
    {
        if (_vm == null) return;
        _suppressDeviceCombo = true;
        foreach (var d in _vm.OutputDevices)
        {
            if (d.Id == _vm.SelectedOutputDeviceId) { DeviceCombo.SelectedItem = d; break; }
        }
        if (DeviceCombo.SelectedItem == null && _vm.OutputDevices.Count > 0)
            DeviceCombo.SelectedIndex = 0;
        _suppressDeviceCombo = false;
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceCombo || _vm == null) return;
        if (DeviceCombo.SelectedItem is MusicViewModel.DeviceItem d)
            _vm.SelectedOutputDeviceId = d.Id;
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;
        _vm.LibraryRoot = folder.Path;
        await _vm.ScanAsync();
    }

    // ── Play routing ──────────────────────────────────────────────────────────
    private void TrackList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_vm != null && TrackList.SelectedItem is MusicTrack track)
            _vm.PlaySelectedLibrary(track);
    }

    private void PlaylistList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_vm != null && PlaylistList.SelectedItem is MusicTrack track)
            _vm.PlaySelectedPlaylist(track);
    }

    // ── Drag library → playlist ────────────────────────────────────────────────
    private void Library_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _dragItems = e.Items.OfType<MusicTrack>().ToList();
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void Library_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        => _dragItems = null;   // drag finished (dropped anywhere or cancelled)

    private void Playlist_DragOver(object sender, DragEventArgs e)
    {
        // External library drag = Copy; internal reorder (no _dragItems) = Move (let ListView handle).
        e.AcceptedOperation = _dragItems != null ? DataPackageOperation.Copy : DataPackageOperation.Move;
    }

    private void Playlist_Drop(object sender, DragEventArgs e)
    {
        if (_vm == null || _dragItems == null) return; // internal reorder handled by ListView
        foreach (var t in _dragItems) _vm.AddToCurrentPlaylist(t);
        _dragItems = null;
    }

    // ── Playlist management ─────────────────────────────────────────────────────
    private async void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var input = new TextBox { PlaceholderText = "Playlist name", AcceptsReturn = false };
        var dialog = new ContentDialog
        {
            Title = "New playlist",
            Content = input,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            _vm.CreatePlaylist(input.Text);
    }

    private void RemoveFromPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_vm != null && PlaylistList.SelectedItem is MusicTrack track)
            _vm.RemoveFromCurrentPlaylist(track);
    }

    private void PlaylistList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete && _vm != null && PlaylistList.SelectedItem is MusicTrack track)
        {
            _vm.RemoveFromCurrentPlaylist(track);
            e.Handled = true;
        }
    }

    // ── Seek ────────────────────────────────────────────────────────────────────
    private void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        => _vm?.BeginSeek();

    private void SeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        => _vm?.EndSeek(SeekSlider.Value);

    private void NpTextColorPick_Click(object sender, RoutedEventArgs e)
        => ShowColorPicker(sender as FrameworkElement, false, _vm?.NpTextColorHex, hex => { if (_vm != null) _vm.NpTextColorHex = hex; });

    private void LyTextColorPick_Click(object sender, RoutedEventArgs e)
        => ShowColorPicker(sender as FrameworkElement, false, _vm?.LyTextColorHex, hex => { if (_vm != null) _vm.LyTextColorHex = hex; });

    private void LyActiveColorPick_Click(object sender, RoutedEventArgs e)
        => ShowColorPicker(sender as FrameworkElement, false, _vm?.LyActiveColorHex, hex => { if (_vm != null) _vm.LyActiveColorHex = hex; });

    private void LyBackgroundColorPick_Click(object sender, RoutedEventArgs e)
        => ShowColorPicker(sender as FrameworkElement, true, _vm?.LyBackgroundColorHex, hex => { if (_vm != null) _vm.LyBackgroundColorHex = hex; });

    private static void ShowColorPicker(FrameworkElement? anchor, bool allowAlpha, string? currentHex, Action<string> apply)
    {
        if (anchor == null) return;

        var picker = new ColorPicker { IsAlphaEnabled = allowAlpha };
        var flyout = new Flyout { Content = picker };
        bool settingInitial = false;
        picker.ColorChanged += (_, e) =>
        {
            if (!settingInitial)
                apply(ToHex(e.NewColor, allowAlpha));
        };

        settingInitial = true;
        picker.Color = TryParseColor(currentHex, out var parsed)
            ? parsed
            : (allowAlpha ? Color.FromArgb(0, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255));
        settingInitial = false;

        flyout.ShowAt(anchor);
    }

    private static bool TryParseColor(string? hex, out Color color)
    {
        color = Color.FromArgb(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(hex)) return false;

        var s = hex.Trim().TrimStart('#');
        try
        {
            if (s.Length == 6)
            {
                color = Color.FromArgb(
                    0xFF,
                    Convert.ToByte(s[..2], 16),
                    Convert.ToByte(s[2..4], 16),
                    Convert.ToByte(s[4..6], 16));
                return true;
            }

            if (s.Length == 8)
            {
                color = Color.FromArgb(
                    Convert.ToByte(s[..2], 16),
                    Convert.ToByte(s[2..4], 16),
                    Convert.ToByte(s[4..6], 16),
                    Convert.ToByte(s[6..8], 16));
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static string ToHex(Color color, bool includeAlpha)
        => includeAlpha
            ? $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
