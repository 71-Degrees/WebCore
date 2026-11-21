/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Timing;

/// <summary>A clock a test moves by hand (for tests of anything that waits on a <see cref="TimeProvider"/>): <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> waits on its timers, and <see cref="Advance"/> fires the ones that have come due, including every period of a periodic timer.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, DueAt(dueTime), period);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
        for (var timer = TakeNextDue(); timer is not null; timer = TakeNextDue())
        {
            timer.Fire();
        }
    }

    // An infinite due time means the timer never comes due, the same as the real Timer. Using MaxValue
    // keeps it out of every Advance without a special case in the firing loop.
    private DateTimeOffset DueAt(TimeSpan dueTime) =>
        dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : _now + dueTime;

    // Takes the earliest timer that has come due, in Due order. A periodic timer stays in the list with its next
    // due time; a one-shot timer leaves it. The lock is released before the callback runs, so a callback may
    // change or dispose timers without deadlocking.
    private ManualTimer? TakeNextDue()
    {
        lock (_timers)
        {
            var next = _timers.MinBy(t => t.Due);
            if (next is null || next.Due > _now)
            {
                return null;
            }

            if (IsPeriodic(next.Period))
            {
                next.Due += next.Period;
            }
            else
            {
                _timers.Remove(next);
            }

            return next;
        }
    }

    // A zero or infinite period is not periodic. Requiring a positive period also stops TakeNextDue from looping
    // forever on a timer whose next due time never moves forward.
    private static bool IsPeriodic(TimeSpan period) => period > TimeSpan.Zero;

    private bool ChangeTimer(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (_timers)
        {
            if (timer.IsDisposed)
            {
                return false;
            }

            timer.Due = DueAt(dueTime);
            timer.Period = period;
            // A timer that already fired was removed as one-shot; changing it arms it again.
            if (!_timers.Contains(timer))
            {
                _timers.Add(timer);
            }

            return true;
        }
    }

    private void RemoveTimer(ManualTimer timer)
    {
        lock (_timers)
        {
            timer.IsDisposed = true;
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset due, TimeSpan period) : ITimer
    {
        public DateTimeOffset Due { get; set; } = due;

        public TimeSpan Period { get; set; } = period;

        public bool IsDisposed { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => owner.ChangeTimer(this, dueTime, period);

        public void Dispose() => owner.RemoveTimer(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
