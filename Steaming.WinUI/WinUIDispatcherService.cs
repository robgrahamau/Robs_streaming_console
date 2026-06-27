using Microsoft.UI.Dispatching;
using Steaming.Core.Services;

namespace Steaming.WinUI;

// Created on the UI thread in App.OnLaunched so GetForCurrentThread() returns the WinUI DispatcherQueue.
public sealed class WinUIDispatcherService : IDispatcherService
{
    private readonly DispatcherQueue _queue;

    public WinUIDispatcherService(DispatcherQueue queue)
    {
        _queue = queue;
    }

    public void Invoke(Action action) =>
        _queue.TryEnqueue(DispatcherQueuePriority.Normal, () => action());
}
