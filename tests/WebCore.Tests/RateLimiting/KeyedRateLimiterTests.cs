/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.RateLimiting;

namespace WebCore.Tests.RateLimiting;

public class KeyedRateLimiterTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_key_may_act_up_to_its_allowance_and_then_is_refused()
    {
        var limiter = new KeyedRateLimiter(3, TimeSpan.FromMinutes(10));

        Assert.True(limiter.TryAcquire("a", Start));
        Assert.True(limiter.TryAcquire("a", Start));
        Assert.True(limiter.TryAcquire("a", Start));
        Assert.False(limiter.TryAcquire("a", Start));
    }

    [Fact]
    public void One_key_using_up_its_allowance_does_not_touch_another()
    {
        var limiter = new KeyedRateLimiter(1, TimeSpan.FromMinutes(10));
        limiter.TryAcquire("abuser", Start);

        Assert.False(limiter.TryAcquire("abuser", Start));
        Assert.True(limiter.TryAcquire("everyone-else", Start));
    }

    [Fact]
    public void The_allowance_comes_back_when_the_window_has_passed()
    {
        var limiter = new KeyedRateLimiter(1, TimeSpan.FromMinutes(10));
        limiter.TryAcquire("a", Start);

        Assert.False(limiter.TryAcquire("a", Start.AddMinutes(9)));
        Assert.True(limiter.TryAcquire("a", Start.AddMinutes(11)));
    }

    [Fact]
    public void Memory_is_bounded_by_forgetting_the_least_used_key_when_too_many_keys_arrive()
    {
        // Memory stays bounded by forgetting one least-used key, not by clearing the table (finding F-R1): k0 keeps its two uses.
        var limiter = new KeyedRateLimiter(2, TimeSpan.FromMinutes(10), maxTrackedKeys: 3);
        limiter.TryAcquire("k0", Start);
        limiter.TryAcquire("k0", Start);
        limiter.TryAcquire("k1", Start);
        limiter.TryAcquire("k2", Start);

        Assert.Equal(3, limiter.TrackedKeys);
        Assert.True(limiter.TryAcquire("one-more", Start));

        Assert.True(limiter.TrackedKeys <= 3);
        Assert.False(limiter.TryAcquire("k0", Start));
    }

    [Fact]
    public void A_known_key_never_triggers_the_clearing()
    {
        var limiter = new KeyedRateLimiter(5, TimeSpan.FromMinutes(10), maxTrackedKeys: 2);
        limiter.TryAcquire("a", Start);
        limiter.TryAcquire("b", Start);

        limiter.TryAcquire("a", Start);

        Assert.Equal(2, limiter.TrackedKeys);
    }
}
