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
    private DateTimeOffset _lastSeen;

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

    /// <summary>When this limiter was last asked, allowed or refused: a refused attempt is still activity, so an abused key stays recent.</summary>
    public DateTimeOffset LastSeen
    {
        get
        {
            lock (_lock)
            {
                return _lastSeen;
            }
        }
    }

    /// <summary>Attempts to record one occurrence of the action now. Returns false if the window is full.</summary>
    public bool TryAcquire(DateTimeOffset? now = null)
    {
        var n = now ?? _time.GetUtcNow();

        lock (_lock)
        {
            _lastSeen = n;
            Prune(n);

            if (_timestamps.Count >= _maxInWindow)
            {
                return false;
            }

            _timestamps.Enqueue(n);
            return true;
        }
    }

    /// <summary>How many uses are still inside the window at <paramref name="now"/>. Used by a keyed table to choose which key to forget.</summary>
    public int CountInWindow(DateTimeOffset now)
    {
        lock (_lock)
        {
            Prune(now);
            return _timestamps.Count;
        }
    }

    // Callers hold _lock. Drops the uses that have aged out of the window, so both TryAcquire and CountInWindow see the same state.
    private void Prune(DateTimeOffset now)
    {
        while (_timestamps.Count > 0 && now - _timestamps.Peek() > _window)
        {
            _timestamps.Dequeue();
        }
    }
}
