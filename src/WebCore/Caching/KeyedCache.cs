/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Collections.Concurrent;

namespace WebCore.Caching;

/// <summary>
/// Many values kept in memory for a short time, one per key (a profile per user, a quiz per id, a search per wording). Same rules as
/// <see cref="SnapshotCache{T}"/>: counted under a name, callers get their own copy when <c>copy</c> is given, and a value read before a write landed is never stored.
/// To use it: read <see cref="Generation"/>, load from the real source, then <see cref="Set"/> passing that generation back.
/// </summary>
public sealed class KeyedCache<TKey, TValue> : ICountsEntries where TKey : notnull where TValue : class
{
    private readonly CacheCounters _counters;
    private readonly TimeSpan _timeToLive;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TValue, TValue>? _copy;
    private readonly int _maxEntries;
    private readonly ConcurrentDictionary<TKey, (TValue Value, DateTimeOffset At)> _entries = new();

    private readonly object _inflightGate = new();
    private readonly Dictionary<TKey, TaskCompletionSource<TValue?>> _inflight = new();

    private int _generation;

    /// <param name="now">An older way to control the clock in a test; <paramref name="timeProvider"/> is the standard one and wins when this is not given.</param>
    public KeyedCache(string name, TimeSpan timeToLive, Func<DateTimeOffset>? now = null, Func<TValue, TValue>? copy = null, int maxEntries = 1000, TimeProvider? timeProvider = null)
    {
        // A cap below one would make MakeRoom empty the cache on every new key; refuse it as a configuration error.
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        _counters = CacheRegistry.For(name);
        _counters.Track(this);
        _timeToLive = timeToLive;
        _now = now ?? (timeProvider ?? TimeProvider.System).GetUtcNow;
        _copy = copy;
        _maxEntries = maxEntries;
    }

    // Explicit, so the public surface does not grow; the registry reads it for the admin and status pages.
    long ICountsEntries.EntryCount => _entries.Count;

    /// <summary>Bumped by every invalidation; pass the value read before loading back to <see cref="Set"/>.</summary>
    public int Generation => Volatile.Read(ref _generation);

    public bool TryGet(TKey key, out TValue value)
    {
        if (_entries.TryGetValue(key, out var entry) && _now() - entry.At < _timeToLive)
        {
            _counters.Hit();
            value = _copy is null ? entry.Value : _copy(entry.Value);
            return true;
        }

        _counters.Miss();
        value = null!;
        return false;
    }

    public void Set(TKey key, TValue value, int generationBeforeLoad)
    {
        if (generationBeforeLoad != Generation)
        {
            return;
        }

        if (_entries.Count >= _maxEntries && !_entries.ContainsKey(key))
        {
            MakeRoom();
        }

        _entries[key] = (_copy is null ? value : _copy(value), _now());
    }

    /// <summary>
    /// The value for <paramref name="key"/>: from memory when fresh, otherwise loaded with <paramref name="load"/> - and when many callers ask for the same key at once
    /// (a crowd opening the same quiz) only the first loads and the rest wait for its answer. A null answer is returned but never stored; a failed load is shared with
    /// the callers that were waiting on it, then forgotten.
    /// </summary>
    public async Task<TValue?> GetOrLoadAsync(TKey key, Func<CancellationToken, Task<TValue?>> load, CancellationToken cancellationToken = default)
    {
        if (Lookup(key, out var cached))
        {
            _counters.Hit();
            return Out(cached);
        }

        TaskCompletionSource<TValue?> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryLead(key, pending, out var leaderTask))
        {
            _counters.Hit(); // shared the leader's read instead of making its own
            var shared = await leaderTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return shared is null ? null : Out(shared);
        }

        // The load is everyone's, so it does not run on the first caller's token: when that caller left (a phone locked), the callers waiting on the same load were all handed its cancellation
        // (the code review of 2026-11-12). Each caller, the first included, may still stop waiting with its own token; the load then finishes for the others and the cache.
        _counters.Miss();
        return await LoadAsLeaderAsync(key, pending, load, CancellationToken.None).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // The first caller for a key becomes the leader and loads; any caller arriving while it loads gets the leader's task to wait on.
    private bool TryLead(TKey key, TaskCompletionSource<TValue?> pending, out Task<TValue?> leaderTask)
    {
        lock (_inflightGate)
        {
            if (_inflight.TryGetValue(key, out var existing))
            {
                leaderTask = existing.Task;
                return false;
            }

            _inflight[key] = pending;
            leaderTask = pending.Task;
            return true;
        }
    }

    private async Task<TValue?> LoadAsLeaderAsync(TKey key, TaskCompletionSource<TValue?> pending, Func<CancellationToken, Task<TValue?>> load, CancellationToken cancellationToken)
    {
        try
        {
            var generation = Generation;
            var loaded = await load(cancellationToken).ConfigureAwait(false);
            if (loaded is not null)
            {
                Set(key, loaded, generation);
            }

            pending.SetResult(loaded);
            return loaded;
        }
        catch (Exception ex)
        {
            pending.SetException(ex);
            throw;
        }
        finally
        {
            lock (_inflightGate)
            {
                // Only this call's own entry: an invalidation may already have replaced it with a newer load.
                if (_inflight.TryGetValue(key, out var current) && ReferenceEquals(current, pending))
                {
                    _inflight.Remove(key);
                }
            }
        }
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _entries.Clear();
        lock (_inflightGate)
        {
            // Callers arriving after a write must not join a load that started before it.
            _inflight.Clear();
        }

        _counters.Invalidated();
    }

    /// <summary>Forgets one key (a write to one person's data): its entry, and any load of it already in flight. The generation still bumps, so a load of
    /// another key that started before this write simply isn't stored - it's just re-read next time.</summary>
    public void Invalidate(TKey key)
    {
        Interlocked.Increment(ref _generation);
        _entries.TryRemove(key, out _);
        lock (_inflightGate)
        {
            _inflight.Remove(key);
        }

        _counters.Invalidated();
    }

    private bool Lookup(TKey key, out TValue value)
    {
        if (_entries.TryGetValue(key, out var entry) && _now() - entry.At < _timeToLive)
        {
            value = entry.Value;
            return true;
        }

        value = null!;
        return false;
    }

    private TValue Out(TValue value) => _copy is null ? value : _copy(value);

    // A full cache used to empty itself, so a few busy keys past the cap reloaded every hot value at once (the code review of 2026-11-12: two or three games preparing their pictures crossed it).
    // Expired entries go first; if it is still full, the oldest tenth goes, so a full cache is not scanned again for every new key.
    private void MakeRoom()
    {
        var now = _now();
        foreach (var entry in _entries)
        {
            if (now - entry.Value.At >= _timeToLive)
            {
                _entries.TryRemove(entry.Key, out _);
            }
        }

        if (_entries.Count < _maxEntries)
        {
            return;
        }

        foreach (var key in _entries.OrderBy(e => e.Value.At).Take(Math.Max(1, _maxEntries / 10)).Select(e => e.Key).ToList())
        {
            _entries.TryRemove(key, out _);
        }
    }
}
