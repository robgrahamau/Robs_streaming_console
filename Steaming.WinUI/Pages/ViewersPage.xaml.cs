using System.Collections.Specialized;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Steaming.Application.ViewModels;

namespace Steaming.WinUI.Pages;

public sealed partial class ViewersPage : Page
{
    private MainViewModel? _vm;

    public ViewersPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _vm = e.Parameter as MainViewModel;
        if (_vm == null) return;

        ViewerList.ItemsSource = _vm.ViewerItems;
        UpdateCount();
        _vm.ViewerItems.CollectionChanged += OnViewersChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_vm != null)
            _vm.ViewerItems.CollectionChanged -= OnViewersChanged;
    }

    private void OnViewersChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(UpdateCount);

    private void UpdateCount()
        => ViewerCountLabel.Text = $"Viewers: {_vm?.ViewerItems.Count ?? 0}";

    private void Refresh_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => _vm?.Viewers.Start();
}
