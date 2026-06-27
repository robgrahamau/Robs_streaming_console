namespace Steaming.Core.Services;

// Abstracts the UI thread dispatcher so ViewModels have zero WPF/WinUI imports
public interface IDispatcherService
{
    void Invoke(Action action);
}
