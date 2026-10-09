/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Caching;

/// <summary>
/// One value (a whole list, an aggregate) kept in memory for a short time in front of something slow. The building block for "read everything, use it often":
/// <list type="bullet">
/// <item>Only one caller loads at a time (a stampede of requests waits for the first, then all read its answer).</item>
/// <item><see cref="Invalidate"/> clears it at once; a load that was in flight when a write landed is used once but never kept (it may already be stale).</item>
/// <item>If the value is mutable, pass <c>copy</c> so every caller gets its own copy and can never change what the next caller sees.</item>
/// <item>Hits, misses and invalidations are counted under <c>name</c> (see <see cref="CacheRegistry"/>).</item>
/// </list>
/// </summary>
public sealed class SnapshotCache<T> : IDisposable where T : class
{
    private readonly CacheCounters _counters;
    private readonly TimeSpan _timeToLive;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<T, T>? _copy;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly object _gate = new();

    private T? _value;
    private DateTimeOffset _loadedAt;
    private int _generation;

    public SnapshotCache(string name, TimeSpan timeToLive, Func<DateTimeOffset>? now = null, Func<T, T>? copy = null)
    {
        _counters = CacheRegistry.For(name);
        _timeToLive = timeToLive;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _copy = copy;
    }

    public async Task<T> GetAsync(Func<CancellationToken, Task<T>> load, CancellationToken cancellationToken = default)
    {
        if (TryGetFresh(out var cached))
        {
            _counters.Hit();
            return Out(cached);
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetFresh(out cached))
            {
                // Someone else loaded it while this caller waited its turn.
                _counters.Hit();
                return Out(cached);
            }

            _counters.Miss();
            int generation;
            lock (_gate)
            {
                generation = _generation;
            }

            var loaded = await load(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _value = _copy is null ? loaded : _copy(loaded);
                    _loadedAt = _now();
                }
            }

            return loaded;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _value = null;
        }

        _counters.Invalidated();
    }

    private bool TryGetFresh(out T value)
    {
        lock (_gate)
        {
            if (_value is not null && _now() - _loadedAt < _timeToLive)
            {
                value = _value;
                return true;
            }
        }

        value = null!;
        return false;
    }

    private T Out(T value) => _copy is null ? value : _copy(value);

    /// <summary>Releases the disposable fields this type owns.</summary>
    public void Dispose()
    {
        _loadLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
