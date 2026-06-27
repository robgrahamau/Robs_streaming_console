using Steaming.Core.Models;

namespace Steaming.Core;

public class EventBus
{
    private readonly List<Func<StreamEvent, Task>> _handlers = [];
    private readonly object _lock = new();

    // Optional per-platform gate. When set and it returns false for an event, the event is dropped
    // before any handler runs — this is the single chokepoint that makes an inactive platform
    // "fully dark" (no inbound chat, alerts, TTS, or activity). Takes the whole event so internal
    // control messages (e.g. the StreamDataUpdated aggregate) can be exempted from gating. Null =
    // pass everything. Wired from app startup to the per-platform Active flags in AppSettings.
    public Func<StreamEvent, bool>? PlatformFilter { get; set; }

    public void Subscribe(Func<StreamEvent, Task> handler)
    {
        lock (_lock) _handlers.Add(handler);
    }

    public void Unsubscribe(Func<StreamEvent, Task> handler)
    {
        lock (_lock) _handlers.Remove(handler);
    }

    public async Task PublishAsync(StreamEvent evt)
    {
        if (PlatformFilter is { } gate && !gate(evt))
            return;

        List<Func<StreamEvent, Task>> snapshot;
        lock (_lock) snapshot = [.._handlers];

        await Task.WhenAll(snapshot.Select(h => h(evt)));
    }
}
