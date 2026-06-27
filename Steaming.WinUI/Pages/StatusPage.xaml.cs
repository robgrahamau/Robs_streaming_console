using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Steaming.Application.ViewModels;

namespace Steaming.WinUI.Pages;

public sealed partial class StatusPage : Page
{
    public StatusPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is MainViewModel vm)
            StatusList.ItemsSource = vm.ServiceStatuses;
    }
}
