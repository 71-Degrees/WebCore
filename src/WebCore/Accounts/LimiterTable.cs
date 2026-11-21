/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using System.Collections.Concurrent;
using WebCore.RateLimiting;

namespace WebCore.Accounts;

/// <summary>
/// The rate limiters behind the account code emails, one per key (an address, a browser or both), with a cap on how many keys are kept. The keys include
/// a browser id the client chooses, so a flood of made-up ones must never reset the limits of real addresses: eviction drops only entries idle for a whole
/// window (their counts are gone anyway); if that is not enough it drops the least-used ones (longest idle among equals) down to 90 percent of the cap,
/// never the whole table.
/// </summary>
internal sealed class LimiterTable(int maxTrackedKeys, TimeSpan window)
{
    private readonly ConcurrentDictionary<string, TrackedLimiter> _limiters = new();
    private readonly object _evictionLock = new();

    /// <summary>Spends one request under <paramref name="key"/>, allowing at most <paramref name="max"/> in the window that ends at <paramref name="now"/>.</summary>
    public bool TryAcquire(string key, int max, DateTimeOffset now)
    {
        if (_limiters.Count > maxTrackedKeys)
        {
            EvictLimiters(now);
        }

        return _limiters.GetOrAdd(key, _ => new TrackedLimiter(new SlidingWindowRateLimiter(max, window))).TryAcquire(now);
    }

    private void EvictLimiters(DateTimeOffset now)
    {
        lock (_evictionLock)
        {
            if (_limiters.Count <= maxTrackedKeys)
            {
                return;
            }

            DropIdleSince((now - window).UtcTicks);
            var excess = _limiters.Count - (maxTrackedKeys * 9 / 10);
            if (excess > 0)
            {
                DropLeastUsed(excess, now);
            }
        }
    }

    private void DropIdleSince(long cutoffTicks)
    {
        foreach (var entry in _limiters.Where(e => e.Value.LastUsedTicks < cutoffTicks).ToList())
        {
            _limiters.TryRemove(entry.Key, out _);
        }
    }

    // Fewest uses left in the window go first, the longest idle among equals: the same rule as KeyedRateLimiter. Dropping the longest idle alone let a
    // victim's address, asked five times and then left alone, be pushed out by a flood of one-use keys (the adversarial review, F-E2).
    private void DropLeastUsed(int count, DateTimeOffset now)
    {
        var victims = _limiters
            .Select(e => (e.Key, Uses: e.Value.CountInWindow(now), e.Value.LastUsedTicks))
            .OrderBy(e => e.Uses)
            .ThenBy(e => e.LastUsedTicks)
            .Take(count)
            .Select(e => e.Key)
            .ToList();
        foreach (var key in victims)
        {
            _limiters.TryRemove(key, out _);
        }
    }

    // A limiter plus when it was last asked, so the table can tell a dead entry from a live one without the limiter having to say.
    private sealed class TrackedLimiter(SlidingWindowRateLimiter limiter)
    {
        private long _lastUsedTicks;

        public long LastUsedTicks => Interlocked.Read(ref _lastUsedTicks);

        public int CountInWindow(DateTimeOffset now) => limiter.CountInWindow(now);

        public bool TryAcquire(DateTimeOffset now)
        {
            Interlocked.Exchange(ref _lastUsedTicks, now.UtcTicks);
            return limiter.TryAcquire(now);
        }
    }
}
