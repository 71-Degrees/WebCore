/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using WebCore.RateLimiting;
using WebCore.Timing;

namespace WebCore.Tests.Adversarial;

// Rate limiters and debouncers under zero and negative windows, clocks that run backwards, extreme timestamps,
// key-table overflow, null inputs and failing actions.
public sealed class LimitersAndTimingAdversarialTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    // ---- SlidingWindowRateLimiter ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void SlidingWindow_NonPositiveLimit_IsRefusedAtConstruction(int max)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowRateLimiter(max, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void SlidingWindow_ZeroWindow_OnlyFreesCapacityWhenTimeMoves()
    {
        var limiter = new SlidingWindowRateLimiter(2, TimeSpan.Zero);

        Assert.True(limiter.TryAcquire(Start));
        Assert.True(limiter.TryAcquire(Start));
        Assert.False(limiter.TryAcquire(Start));
        Assert.True(limiter.TryAcquire(Start.AddTicks(1)));
    }

    [Fact]
    public void SlidingWindow_ClockRunningBackwards_KeepsTheLimitUntilTheWindowPassesTheLaterTimestamp()
    {
        var limiter = new SlidingWindowRateLimiter(1, TimeSpan.FromMinutes(10));
        Assert.True(limiter.TryAcquire(Start));

        Assert.False(limiter.TryAcquire(Start.AddMinutes(-5)));
        Assert.True(limiter.TryAcquire(Start.AddMinutes(10).AddTicks(1)));
    }

    [Fact]
    public void SlidingWindow_ExtremeTimestamps_AgeOutWithoutOverflow()
    {
        // Checked: the tick difference between MinValue and MaxValue still fits a TimeSpan, so the old entry simply ages out.
        var limiter = new SlidingWindowRateLimiter(5, TimeSpan.FromMinutes(1));
        limiter.TryAcquire(DateTimeOffset.MinValue);

        Assert.True(limiter.TryAcquire(DateTimeOffset.MaxValue));
    }

    [Fact]
    public void SlidingWindow_ExactlyAtTheWindowEdge_IsStillCounted()
    {
        var limiter = new SlidingWindowRateLimiter(1, TimeSpan.FromMinutes(10));
        Assert.True(limiter.TryAcquire(Start));

        Assert.False(limiter.TryAcquire(Start.AddMinutes(10)));
    }

    // ---- KeyedRateLimiter ----

    [Fact]
    public void KeyedRateLimiter_NullKey_ThrowsArgumentNull()
    {
        var limiter = new KeyedRateLimiter(1, TimeSpan.FromMinutes(1));

        Assert.Throws<ArgumentNullException>(() => limiter.TryAcquire(null!));
    }

    [Fact]
    public void KeyedRateLimiter_EmptyStringKey_IsAKeyLikeAnyOther()
    {
        var limiter = new KeyedRateLimiter(1, TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryAcquire(string.Empty, Start));
        Assert.False(limiter.TryAcquire(string.Empty, Start));
    }

    [Fact]
    public void KeyedRateLimiter_KeyCaseIsSignificant()
    {
        var limiter = new KeyedRateLimiter(1, TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryAcquire("Addr", Start));
        Assert.True(limiter.TryAcquire("addr", Start));
    }

    [Fact(Skip = "FINDING F-R1 (security): when the key table is full it is cleared, so a flood of distinct keys resets every key's limit")]
    public void KeyedRateLimiter_FloodOfDistinctKeys_DoesNotResetAnExistingKey_Finding()
    {
        var limiter = new KeyedRateLimiter(1, TimeSpan.FromMinutes(10), maxTrackedKeys: 2);
        Assert.True(limiter.TryAcquire("victim", Start));
        Assert.True(limiter.TryAcquire("other1", Start));
        Assert.True(limiter.TryAcquire("bot", Start));

        Assert.False(limiter.TryAcquire("victim", Start));
    }

    [Fact]
    public void KeyedRateLimiter_FloodOfDistinctKeys_ClearsTheTable_AsRecorded()
    {
        var limiter = new KeyedRateLimiter(1, TimeSpan.FromMinutes(10), maxTrackedKeys: 2);
        limiter.TryAcquire("victim", Start);
        limiter.TryAcquire("other1", Start);

        limiter.TryAcquire("bot", Start);

        Assert.Equal(1, limiter.TrackedKeys);
    }

    // ---- AccountAttemptLimiter ----

    [Fact]
    public void AccountAttemptLimiter_ScaleOfIntMaxValue_OverflowsTheCeilingAndIsRefused()
    {
        // 10 * int.MaxValue wraps negative, and the window limiter rejects that at construction.
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => new AccountAttemptLimiter(scale: int.MaxValue));
    }

    [Fact]
    public void AccountAttemptLimiter_ZeroOrNegativeScale_BehavesAsOne()
    {
        var limiter = new AccountAttemptLimiter(scale: -4);

        Assert.True(limiter.TryAcquireAddress("1.2.3.4"));
    }

    [Fact]
    public void AccountAttemptLimiter_EmptyOrNullAddress_SkipsThePerAddressLimitButNotTheGlobalOne()
    {
        var limiter = new AccountAttemptLimiter();

        var passed = Enumerable.Range(0, 30).Count(_ => limiter.TryAcquire(null));

        Assert.Equal(AccountAttemptLimiter.DefaultGlobalMax, passed);
    }

    [Fact]
    public void AccountAttemptLimiter_GlobalCeilingAcrossManyAddresses_StopsTheTwentyFirst()
    {
        var limiter = new AccountAttemptLimiter();

        var passed = Enumerable.Range(0, 30).Count(i => limiter.TryAcquire("10.0.0." + i));

        Assert.Equal(AccountAttemptLimiter.DefaultGlobalMax, passed);
    }

    [Fact]
    public void AccountAttemptLimiter_EmailIsNormalisedBeforeTheAccountBucket()
    {
        var limiter = new AccountAttemptLimiter(globalMax: 1000);
        var results = new[] { "a@b.co", "  A@B.CO ", "A@b.co" }
            .Select(e => limiter.TryAcquireLogin("1.1.1.1", e))
            .ToList();
        var extra = Enumerable.Range(0, 10).Select(_ => limiter.TryAcquireLogin("1.1.1.1", "a@b.co")).ToList();

        // Ten per account per fifteen minutes in total, however the address is written: three used above, seven more allowed.
        Assert.All(results, Assert.True);
        Assert.Equal(7, extra.Count(allowed => allowed));
    }

    [Fact]
    public void AccountAttemptLimiter_NullEmailOnLogin_ThrowsNullReference()
    {
        var limiter = new AccountAttemptLimiter();

        Assert.Throws<NullReferenceException>(() => limiter.TryAcquireLogin("1.1.1.1", null!));
    }

    [Fact]
    public void AccountAttemptLimiter_GlobalMaxOfZero_IsRaisedToOne()
    {
        var limiter = new AccountAttemptLimiter(globalMax: 0);

        Assert.True(limiter.TryAcquire("1.1.1.1"));
        Assert.False(limiter.TryAcquire("2.2.2.2"));
    }

    // ---- KeyedDebouncer and Debouncer ----

    [Fact]
    public void KeyedDebouncer_NullErrorHook_IsRefusedAtConstruction()
    {
        Assert.Throws<ArgumentNullException>(() => new KeyedDebouncer<string>(TimeSpan.FromSeconds(1), null!));
    }

    [Fact]
    public void KeyedDebouncer_NullAction_ThrowsArgumentNull()
    {
        using var debouncer = new KeyedDebouncer<string>(TimeSpan.FromSeconds(1), _ => { });

        Assert.Throws<ArgumentNullException>(() => debouncer.Trigger("k", null!));
    }

    [Fact]
    public async Task KeyedDebouncer_NegativeDelay_ReportsTheErrorThroughTheHook()
    {
        var errors = new List<Exception>();
        // On the system clock, Task.Delay rejects a negative span at once; on the fake clock the delay would just wait for Advance.
        using var debouncer = new KeyedDebouncer<string>(TimeSpan.FromMilliseconds(-2), errors.Add);

        debouncer.Trigger("k", _ => Task.CompletedTask);
        for (var i = 0; i < 200 && errors.Count == 0; i++)
        {
            await Task.Delay(5);
        }

        Assert.Single(errors);
        Assert.IsType<ArgumentOutOfRangeException>(errors[0]);
    }

    [Fact]
    public async Task KeyedDebouncer_ActionThrows_ReachesTheErrorHook()
    {
        var time = new ManualTimeProvider();
        var errors = new List<Exception>();
        using var debouncer = new KeyedDebouncer<string>(TimeSpan.FromSeconds(1), errors.Add, time);

        debouncer.Trigger("k", _ => throw new InvalidOperationException("action failed"));
        time.Advance(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 200 && errors.Count == 0; i++)
        {
            await Task.Delay(5);
        }

        Assert.Single(errors);
        Assert.IsType<InvalidOperationException>(errors[0]);
    }

    [Fact]
    public void Debouncer_TriggerAfterDispose_NeverRunsTheAction()
    {
        var time = new ManualTimeProvider();
        var ran = false;
        var debouncer = new Debouncer(TimeSpan.FromSeconds(1), _ => { }, time);
        debouncer.Dispose();

        debouncer.Trigger(_ => { ran = true; return Task.CompletedTask; });
        time.Advance(TimeSpan.FromHours(1));

        Assert.False(ran);
    }

    [Fact]
    public void Debouncer_CancelWithNothingPending_AndCancelTwice_AreNoOps()
    {
        var debouncer = new Debouncer(TimeSpan.FromSeconds(1), _ => { }, new ManualTimeProvider());

        Assert.Null(Record.Exception(() =>
        {
            debouncer.Cancel();
            debouncer.Cancel();
            debouncer.Dispose();
        }));
    }

    [Fact]
    public void Debouncer_ManyTriggersInsideTheDelay_RunTheActionOnce()
    {
        var time = new ManualTimeProvider();
        var runs = 0;
        var debouncer = new Debouncer(TimeSpan.FromSeconds(1), _ => { }, time);

        for (var i = 0; i < 50; i++)
        {
            debouncer.Trigger(_ => { runs++; return Task.CompletedTask; });
            time.Advance(TimeSpan.FromMilliseconds(10));
        }

        time.Advance(TimeSpan.FromSeconds(2));
        debouncer.Dispose();

        Assert.Equal(1, runs);
    }

    // ---- ManualTimeProvider ----

    [Fact]
    public void ManualTimeProvider_AdvanceByZero_FiresTimersDueNow()
    {
        var time = new ManualTimeProvider();
        var fired = 0;
        using var timer = time.CreateTimer(_ => fired++, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);

        time.Advance(TimeSpan.Zero);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void ManualTimeProvider_NegativeAdvance_MovesBackAndFiresNothing()
    {
        var time = new ManualTimeProvider();
        var fired = 0;
        using var timer = time.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);

        time.Advance(TimeSpan.FromSeconds(-10));

        Assert.Equal(0, fired);
    }

    [Fact]
    public void ManualTimeProvider_InfiniteTimer_FiresOnTheNextAdvance_AsRecorded_Finding()
    {
        // An infinite due time is stored as now minus one millisecond, so the timer is treated as already due.
        var time = new ManualTimeProvider();
        var fired = 0;
        using var timer = time.CreateTimer(_ => fired++, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        time.Advance(TimeSpan.Zero);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void ManualTimeProvider_PeriodicTimer_FiresOnlyOnce()
    {
        var time = new ManualTimeProvider();
        var fired = 0;
        using var timer = time.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(1, fired);
    }
}
