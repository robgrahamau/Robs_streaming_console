using System.Collections.Specialized;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Steaming.Application.ViewModels;

namespace Steaming.WinUI.Pages;

public sealed partial class ActivityPage : Page
{
    private MainViewModel? _vm;

    public ActivityPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _vm = e.Parameter as MainViewModel;
        if (_vm == null) return;

        ActivityList.ItemsSource = _vm.ActivityItems;
        UpdateCount();

        // Never preload DB history — show only live events from the current app session.
        _vm.ActivityHistoryLoaded = true;

        _vm.ActivityItems.CollectionChanged += OnActivityChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_vm != null)
            _vm.ActivityItems.CollectionChanged -= OnActivityChanged;
    }

    private void Clear_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => _vm?.ClearActivity();

    private async void LoadDate_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_vm == null || HistoryDatePicker.Date is not DateTimeOffset dto) return;
        var date = DateOnly.FromDateTime(dto.LocalDateTime);
        _vm.ClearActivity();
        try
        {
            var entries = await Task.Run(() => _vm.Activity.GetByDate(date));
            foreach (var entry in entries)
                _vm.AddActivityLine($"{entry.Timestamp.LocalDateTime:HH:mm:ss}  [{entry.Platform}] {entry.Username} — {entry.Description}");
        }
        catch { }
    }

    private void OnActivityChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(UpdateCount);

    private void UpdateCount()
        => CountLabel.Text = $"{_vm?.ActivityItems.Count ?? 0} events";
}
