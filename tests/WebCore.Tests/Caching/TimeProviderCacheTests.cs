/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Caching;

namespace WebCore.Tests.Caching;

public class TimeProviderCacheTests
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task ASnapshotExpiresWhenTheTimeProviderSaysItsTimeToLiveIsUp()
    {
        var time = new ManualTime();
        using var cache = new SnapshotCache<string>("snapshot-tp-" + Guid.NewGuid().ToString("N"), TimeSpan.FromSeconds(30), timeProvider: time);
        var loads = 0;
        Task<string> Load(CancellationToken _) => Task.FromResult("v" + ++loads);

        Assert.Equal("v1", await cache.GetAsync(Load));
        Assert.Equal("v1", await cache.GetAsync(Load));

        time.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal("v2", await cache.GetAsync(Load));
    }

    [Fact]
    public async Task AKeyedEntryExpiresWhenTheTimeProviderSaysItsTimeToLiveIsUp()
    {
        var time = new ManualTime();
        var cache = new KeyedCache<string, string>("keyed-tp-" + Guid.NewGuid().ToString("N"), TimeSpan.FromSeconds(30), timeProvider: time);
        var loads = 0;
        Task<string?> Load(CancellationToken _) => Task.FromResult<string?>("v" + ++loads);

        Assert.Equal("v1", await cache.GetOrLoadAsync("k", Load));
        Assert.Equal("v1", await cache.GetOrLoadAsync("k", Load));

        time.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal("v2", await cache.GetOrLoadAsync("k", Load));
    }
}
