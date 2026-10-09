/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.RateLimiting;

namespace WebCore.Tests.RateLimiting;

public class SlidingWindowRateLimiterTests
{
    [Fact]
    public void TryAcquire_UnderLimit_Succeeds()
    {
        var limiter = new SlidingWindowRateLimiter(3, TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
    }

    [Fact]
    public void TryAcquire_AtLimit_Rejects()
    {
        var limiter = new SlidingWindowRateLimiter(2, TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire());
    }

    [Fact]
    public void TryAcquire_AfterWindowElapses_AllowsAgain()
    {
        var limiter = new SlidingWindowRateLimiter(1, TimeSpan.FromMinutes(1));
        var t0 = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire(t0));
        Assert.False(limiter.TryAcquire(t0.AddSeconds(30)));
        Assert.True(limiter.TryAcquire(t0.AddMinutes(1).AddSeconds(1)));
    }

    [Fact]
    public void TryAcquire_OnlyOldEntriesExpire_RecentOnesStillCount()
    {
        var limiter = new SlidingWindowRateLimiter(2, TimeSpan.FromMinutes(1));
        var t0 = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire(t0));
        Assert.True(limiter.TryAcquire(t0.AddSeconds(50)));
        // t0 has expired by t0+61s, but the second one (t0+50s) hasn't yet.
        Assert.True(limiter.TryAcquire(t0.AddSeconds(61)));
        Assert.False(limiter.TryAcquire(t0.AddSeconds(62)));
    }

    [Fact]
    public void Constructor_NonPositiveMax_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowRateLimiter(0, TimeSpan.FromMinutes(1)));
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void TheWindowFollowsTheTimeProviderWhenNoTimeIsPassedIn()
    {
        var time = new ManualTime();
        var limiter = new SlidingWindowRateLimiter(1, TimeSpan.FromMinutes(1), time);

        Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire());

        time.Advance(TimeSpan.FromMinutes(2));

        Assert.True(limiter.TryAcquire());
    }
}
