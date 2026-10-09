/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.RateLimiting;

/// <summary>
/// A small, dependency-free sliding-window limiter for guarding a single action (e.g. "feedback
/// submissions") against being spammed - not per-user, just a global ceiling on how often the
/// action can succeed across everyone, which is enough to blunt a runaway bot/bug without needing
/// per-identity tracking.
/// </summary>
public class SlidingWindowRateLimiter
{
    private readonly int _maxInWindow;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Queue<DateTimeOffset> _timestamps = new();

    public SlidingWindowRateLimiter(int maxInWindow, TimeSpan window, TimeProvider? timeProvider = null)
    {
        if (maxInWindow <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInWindow), "Must allow at least one action per window.");
        }

        _maxInWindow = maxInWindow;
        _window = window;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Attempts to record one occurrence of the action now. Returns false if the window is full.</summary>
    public bool TryAcquire(DateTimeOffset? now = null)
    {
        var n = now ?? _time.GetUtcNow();

        lock (_lock)
        {
            while (_timestamps.Count > 0 && n - _timestamps.Peek() > _window)
            {
                _timestamps.Dequeue();
            }

            if (_timestamps.Count >= _maxInWindow)
            {
                return false;
            }

            _timestamps.Enqueue(n);
            return true;
        }
    }
}
