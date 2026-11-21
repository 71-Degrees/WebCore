/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Collections.Concurrent;

namespace WebCore.RateLimiting;

/// <summary>
/// A <see cref="SlidingWindowRateLimiter"/> per key (an address, an account), so one abusive client uses up only its own allowance instead of a ceiling shared by everyone. At most
/// <c>maxTrackedKeys</c> keys are remembered. When the table is full, keys with no use left in the window are forgotten first, then the least-used keys. Memory stays bounded.
/// </summary>
public sealed class KeyedRateLimiter
{
    private readonly int _maxInWindow;
    private readonly TimeSpan _window;
    private readonly int _maxTrackedKeys;
    private readonly TimeProvider? _timeProvider;
    private readonly ConcurrentDictionary<string, SlidingWindowRateLimiter> _limiters = new(StringComparer.Ordinal);

    public KeyedRateLimiter(int maxInWindow, TimeSpan window, int maxTrackedKeys = 10_000, TimeProvider? timeProvider = null)
    {
        // A table that holds nothing would refuse every key's first use by forgetting it at once; refuse the size as a configuration error.
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTrackedKeys, 1);

        _maxInWindow = maxInWindow;
        _window = window;
        _maxTrackedKeys = maxTrackedKeys;
        _timeProvider = timeProvider;
    }

    /// <summary>The number of keys currently remembered.</summary>
    public int TrackedKeys => _limiters.Count;

    /// <summary>True when this key may act again now (and counts it); false once it has used its allowance for the window.</summary>
    public bool TryAcquire(string key, DateTimeOffset? now = null)
    {
        var n = now ?? (_timeProvider ?? TimeProvider.System).GetUtcNow();
        if (_limiters.Count >= _maxTrackedKeys && !_limiters.ContainsKey(key))
        {
            MakeRoom(n);
        }

        return _limiters.GetOrAdd(key, _ => new SlidingWindowRateLimiter(_maxInWindow, _window, _timeProvider)).TryAcquire(n);
    }

    // Clearing the whole table let a flood of new keys reset the key being abused (finding F-R1). Instead, idle keys go first, and a
    // key near its limit is kept: a flood must now spend as many uses per key as the victim has to push it out.
    private void MakeRoom(DateTimeOffset now)
    {
        foreach (var entry in _limiters)
        {
            if (entry.Value.CountInWindow(now) == 0)
            {
                _limiters.TryRemove(entry.Key, out _);
            }
        }

        if (_limiters.Count < _maxTrackedKeys)
        {
            return;
        }

        foreach (var key in LeastUsedKeys(now, Math.Max(1, _maxTrackedKeys / 10)))
        {
            _limiters.TryRemove(key, out _);
        }
    }

    // Fewest uses in the window first; among equals, the one seen longest ago goes first.
    private IEnumerable<string> LeastUsedKeys(DateTimeOffset now, int count) =>
        _limiters
            .Select(e => (Key: e.Key, Uses: e.Value.CountInWindow(now), e.Value.LastSeen))
            .OrderBy(e => e.Uses)
            .ThenBy(e => e.LastSeen)
            .Take(count)
            .Select(e => e.Key)
            .ToList();
}
