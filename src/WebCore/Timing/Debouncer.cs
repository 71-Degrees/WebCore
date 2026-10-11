/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Timing;

/// <summary>The one-thing version of <see cref="KeyedDebouncer{TKey}"/>: the action runs once triggers have stopped for the delay, and the latest trigger wins.</summary>
public sealed class Debouncer : IDisposable
{
    private readonly KeyedDebouncer<int> _inner;

    public Debouncer(TimeSpan delay, Action<Exception> onError, TimeProvider? time = null)
    {
        _inner = new KeyedDebouncer<int>(delay, onError, time);
    }

    /// <summary>Runs <paramref name="action"/> after the delay unless this is triggered again first.</summary>
    public void Trigger(Func<CancellationToken, Task> action) => _inner.Trigger(0, action);

    /// <summary>Stops whatever is waiting or running.</summary>
    public void Cancel() => _inner.Cancel(0);

    public void Dispose() => _inner.Dispose();
}
