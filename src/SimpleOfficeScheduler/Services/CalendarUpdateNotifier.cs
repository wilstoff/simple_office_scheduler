namespace SimpleOfficeScheduler.Services;

public class CalendarUpdateNotifier
{
    private readonly object _lock = new();
    private readonly List<Action> _subscribers = new();

    public IDisposable Subscribe(Action handler)
    {
        lock (_lock) _subscribers.Add(handler);
        return new Unsubscriber(() => { lock (_lock) _subscribers.Remove(handler); });
    }

    public void Notify()
    {
        List<Action> snapshot;
        lock (_lock) snapshot = _subscribers.ToList();
        foreach (var handler in snapshot)
        {
            try { handler(); }
            catch { /* swallow per-subscriber errors */ }
        }
    }

    private sealed class Unsubscriber(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
