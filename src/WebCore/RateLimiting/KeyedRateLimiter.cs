/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Collections.Concurrent;

namespace WebCore.RateLimiting;

/// <summary>
/// A <see cref="SlidingWindowRateLimiter"/> per key (an address, an account), so one abusive client uses up only its own allowance instead of a ceiling shared by everyone. At most
/// <c>maxTrackedKeys</c> keys are remembered: past that the table is cleared, which lets an attacker who invents thousands of keys reset the counts - acceptable because that costs the attacker
/// more than it gains and the shared ceilings behind this one still apply. Memory stays bounded.
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
        if (_limiters.Count >= _maxTrackedKeys && !_limiters.ContainsKey(key))
        {
            _limiters.Clear();
        }

        return _limiters.GetOrAdd(key, _ => new SlidingWindowRateLimiter(_maxInWindow, _window, _timeProvider)).TryAcquire(now);
    }
}
