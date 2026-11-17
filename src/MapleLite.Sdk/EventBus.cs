namespace MapleLite.Sdk;

/// <summary>The host's event bus. Thread-safe; a throwing handler never stops the others.</summary>
public sealed class EventBus : IEventBus
{
    readonly object _lock = new();
    readonly Dictionary<Type, List<Action<object>>> _handlers = new();
    readonly Dictionary<Type, object> _last = new();

    /// <summary>Called with each handler exception (the host logs it).</summary>
    public Action<Exception>? OnHandlerError { get; set; }

    public void Publish<T>(T evt) where T : class
    {
        Action<object>[] handlers;
        lock (_lock)
        {
            _last[typeof(T)] = evt;
            handlers = _handlers.TryGetValue(typeof(T), out var list) ? list.ToArray() : [];
        }
        foreach (var h in handlers)
        {
            try { h(evt); }
            catch (Exception e) { OnHandlerError?.Invoke(e); }
        }
    }

    public IDisposable Subscribe<T>(Action<T> handler) where T : class
    {
        Action<object> wrapped = o => handler((T)o);
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(T), out var list)) _handlers[typeof(T)] = list = [];
            list.Add(wrapped);
        }
        return new Unsubscribe(() => { lock (_lock) _handlers[typeof(T)].Remove(wrapped); });
    }

    public T? Last<T>() where T : class
    {
        lock (_lock) return _last.TryGetValue(typeof(T), out var v) ? (T)v : null;
    }

    sealed class Unsubscribe(Action action) : IDisposable
    {
        Action? _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
