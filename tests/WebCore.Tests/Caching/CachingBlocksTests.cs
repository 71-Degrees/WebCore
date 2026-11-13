/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Caching;

namespace WebCore.Tests.Caching;

public class CachingBlocksTests
{
    private DateTimeOffset _now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static string Name() => "test." + Guid.NewGuid().ToString("N");

    // ---- SnapshotCache ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Snapshot_LoadsOnce_UntilTheTimeToLiveLapses()
    {
        var cache = new SnapshotCache<string>(Name(), TimeSpan.FromSeconds(30), () => _now);
        var loads = 0;
        Task<string> Load(CancellationToken _) => Task.FromResult("v" + ++loads);

        Assert.Equal("v1", await cache.GetAsync(Load));
        Assert.Equal("v1", await cache.GetAsync(Load));

        _now = _now.AddSeconds(31);
        Assert.Equal("v2", await cache.GetAsync(Load));
    }

    [Fact]
    public async Task Snapshot_AStampedeOfCallers_SharesOneLoad()
    {
        var cache = new SnapshotCache<string>(Name(), TimeSpan.FromSeconds(30), () => _now);
        var loads = 0;

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => cache.GetAsync(async _ =>
        {
            Interlocked.Increment(ref loads);
            await Task.Delay(100, _);
            return "one";
        })));

        Assert.Equal(1, loads);
        Assert.All(results, r => Assert.Equal("one", r));
    }

    [Fact]
    public async Task Snapshot_InvalidateClearsAtOnce()
    {
        var cache = new SnapshotCache<string>(Name(), TimeSpan.FromSeconds(30), () => _now);
        var loads = 0;
        Task<string> Load(CancellationToken _) => Task.FromResult("v" + ++loads);
        await cache.GetAsync(Load);

        cache.Invalidate();

        Assert.Equal("v2", await cache.GetAsync(Load));
    }

    [Fact]
    public async Task Snapshot_ALoadInFlightWhenAWriteLands_IsNotKept()
    {
        var cache = new SnapshotCache<string>(Name(), TimeSpan.FromSeconds(30), () => _now);
        var release = new TaskCompletionSource();
        var loads = 0;

        var slow = cache.GetAsync(async _ =>
        {
            loads++;
            await release.Task;
            return "stale";
        });
        cache.Invalidate(); // a write lands while the load is still running
        release.SetResult();
        Assert.Equal("stale", await slow); // this caller still gets its answer...

        Assert.Equal("fresh", await cache.GetAsync(_ => Task.FromResult("fresh"))); // ...but it was not remembered
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task Snapshot_WithACopyFunction_EveryCallerGetsItsOwn()
    {
        var cache = new SnapshotCache<List<int>>(Name(), TimeSpan.FromSeconds(30), () => _now, copy: l => l.ToList());

        var first = await cache.GetAsync(_ => Task.FromResult(new List<int> { 1, 2, 3 }));
        first.Add(99);
        var second = await cache.GetAsync(_ => Task.FromResult(new List<int> { 7 }));
        second.Clear();
        var third = await cache.GetAsync(_ => Task.FromResult(new List<int> { 7 }));

        Assert.Equal(new[] { 1, 2, 3 }, third);
    }

    [Fact]
    public async Task Snapshot_AFailedLoad_IsNotRemembered()
    {
        var cache = new SnapshotCache<string>(Name(), TimeSpan.FromSeconds(30), () => _now);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(_ => throw new InvalidOperationException("boom")));

        Assert.Equal("ok", await cache.GetAsync(_ => Task.FromResult("ok")));
    }

    // ---- KeyedCache ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Keyed_StoresPerKey_ForTheTimeToLive()
    {
        var cache = new KeyedCache<string, string>(Name(), TimeSpan.FromSeconds(30), () => _now);
        cache.Set("a", "1", cache.Generation);

        Assert.True(cache.TryGet("a", out var hit));
        Assert.Equal("1", hit);
        Assert.False(cache.TryGet("b", out _));

        _now = _now.AddSeconds(31);
        Assert.False(cache.TryGet("a", out _));
    }

    [Fact]
    public void Keyed_AValueLoadedBeforeAWrite_IsNeverStored()
    {
        var cache = new KeyedCache<string, string>(Name(), TimeSpan.FromSeconds(30), () => _now);
        var generation = cache.Generation;

        cache.Invalidate(); // a write lands after the read started
        cache.Set("a", "stale", generation);

        Assert.False(cache.TryGet("a", out _));
    }

    [Fact]
    public void Keyed_WithACopyFunction_CallersCannotChangeTheStoredValue()
    {
        var cache = new KeyedCache<string, List<int>>(Name(), TimeSpan.FromSeconds(30), () => _now, copy: l => l.ToList());
        var original = new List<int> { 1 };
        cache.Set("a", original, cache.Generation);
        original.Add(2);

        Assert.True(cache.TryGet("a", out var got));
        got.Add(3);

        Assert.True(cache.TryGet("a", out var again));
        Assert.Equal(new[] { 1 }, again);
    }

    [Fact]
    public void Keyed_StaysWithinItsSizeLimit()
    {
        var cache = new KeyedCache<int, string>(Name(), TimeSpan.FromMinutes(5), () => _now, maxEntries: 10);
        for (var i = 0; i < 50; i++)
        {
            cache.Set(i, "x", cache.Generation);
        }

        var kept = Enumerable.Range(0, 50).Count(i => cache.TryGet(i, out _));
        Assert.InRange(kept, 1, 10);
    }

    [Fact]
    public void Keyed_AtItsLimit_DropsTheOldest_NotEverything()
    {
        var cache = new KeyedCache<int, string>(Name(), TimeSpan.FromMinutes(5), () => _now, maxEntries: 10);
        for (var i = 0; i < 10; i++)
        {
            cache.Set(i, "x", cache.Generation);
            _now = _now.AddSeconds(1);
        }

        cache.Set(10, "x", cache.Generation);

        Assert.False(cache.TryGet(0, out _));                                    // the oldest went
        Assert.All(Enumerable.Range(1, 10), i => Assert.True(cache.TryGet(i, out _)));   // the rest stayed
    }

    [Fact]
    public void Keyed_AtItsLimit_DropsExpiredEntriesFirst()
    {
        var cache = new KeyedCache<int, string>(Name(), TimeSpan.FromSeconds(30), () => _now, maxEntries: 4);
        cache.Set(0, "fresh", cache.Generation);
        _now = _now.AddSeconds(-60);
        for (var i = 1; i < 4; i++)
        {
            cache.Set(i, "old", cache.Generation);
        }

        _now = _now.AddSeconds(60);
        cache.Set(4, "new", cache.Generation);

        Assert.True(cache.TryGet(0, out _));
        Assert.True(cache.TryGet(4, out _));
    }

    // ---- counters --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Counters_RecordHitsMissesAndInvalidations_UnderTheCachesName()
    {
        var name = Name();
        var cache = new SnapshotCache<string>(name, TimeSpan.FromSeconds(30), () => _now);

        await cache.GetAsync(_ => Task.FromResult("v"));      // miss
        await cache.GetAsync(_ => Task.FromResult("v"));      // hit
        await cache.GetAsync(_ => Task.FromResult("v"));      // hit
        cache.Invalidate();

        var snapshot = CacheRegistry.Snapshot().Single(s => s.Name == name);
        Assert.Equal(2, snapshot.Hits);
        Assert.Equal(1, snapshot.Misses);
        Assert.Equal(1, snapshot.Invalidations);
        Assert.Equal(2.0 / 3.0, snapshot.HitRatio!.Value, 3);
    }

    [Fact]
    public void Counters_HitRatioIsNullBeforeAnyRead() =>
        Assert.Null(new CacheSnapshot("x", 0, 0, 0).HitRatio);

    // ---- KeyedCache.GetOrLoadAsync (single flight) -------------------------------------------------------------------------------------

    [Fact]
    public async Task GetOrLoad_ACrowdAskingForOneKey_SharesOneLoad()
    {
        var cache = new KeyedCache<string, string>(Name(), TimeSpan.FromSeconds(30), () => _now);
        var loads = 0;

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => cache.GetOrLoadAsync("q", async _ =>
        {
            Interlocked.Increment(ref loads);
            await Task.Delay(100, _);
            return "quiz";
        })));

        Assert.Equal(1, loads);
        Assert.All(results, r => Assert.Equal("quiz", r));
        Assert.Equal("again", await cache.GetOrLoadAsync("q", _ => Task.FromResult<string?>("again")) is "quiz" ? "again" : "wrong"); // now served from memory
    }

    [Fact]
    public async Task GetOrLoad_DifferentKeys_LoadIndependently()
    {
        var cache = new KeyedCache<string, string>(Name(), TimeSpan.FromSeconds(30), () => _now);
        var loads = 0;

        await Task.WhenAll(new[] { "a", "b", "c" }.Select(k => cache.GetOrLoadAsync(k, async _ => { Interlocked.Increment(ref loads); await Task.Delay(20, _); return k; })));

        Assert.Equal(3, loads);
    }

    [Fact]
    public async Task GetOrLoad_ANullAnswer_IsReturnedButNeverStored()
    {
        var cache = new KeyedCache<string, string>(Name(), TimeSpan.FromSeconds(30), () => _now);

        Assert.Null(await cache.GetOrLoadAsync("gone", _ => Task.FromResult<string?>(null)));
        Assert.Equal("appeared", await cache.GetOrLoadAsync("gone", _ => Task.FromResult<string?>("appeared")));
    }

    [Fact]
    public async Task GetOrLoad_AFailedLoad_IsSharedWithThoseWaiting_ThenForgotten()
    {
        var cache = new KeyedCache<string, string>(Name(), TimeSpan.FromSeconds(30), () => _now);
        var release = new TaskCompletionSource();

        var first = cache.GetOrLoadAsync("k", async _ => { await release.Task; throw new InvalidOperationException("boom"); });
        var second = cache.GetOrLoadAsync("k", _ => Task.FromResult<string?>("never used"));
        release.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Equal("ok", await cache.GetOrLoadAsync("k", _ => Task.FromResult<string?>("ok")));
    }

    [Fact]
    public async Task GetOrLoad_TheFirstCallerLeaving_DoesNotCancelTheLoadTheOthersWaitOn()
    {
        var cache = new KeyedCache<string, string>(Name(), TimeSpan.FromSeconds(30), () => _now);
        var release = new TaskCompletionSource();
        using var leaving = new CancellationTokenSource();
        var tokenSeen = new CancellationToken(true);

        var first = cache.GetOrLoadAsync("pic", async ct => { tokenSeen = ct; await release.Task; return "bytes"; }, leaving.Token);
        var second = cache.GetOrLoadAsync("pic", _ => Task.FromResult<string?>("never used"));
        await leaving.CancelAsync();   // the first phone locks

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();

        Assert.Equal("bytes", await second);
        Assert.False(tokenSeen.CanBeCanceled);
        Assert.Equal("bytes", await cache.GetOrLoadAsync("pic", _ => Task.FromResult<string?>("not called")));   // and it was kept
    }

    [Fact]
    public async Task GetOrLoad_AWriteDuringALoad_MeansLaterCallersLoadFresh()
    {
        var cache = new KeyedCache<string, string>(Name(), TimeSpan.FromSeconds(30), () => _now);
        var release = new TaskCompletionSource();
        var slow = cache.GetOrLoadAsync("k", async _ => { await release.Task; return "stale"; });

        cache.Invalidate(); // a write lands while that load is running
        var fresh = await cache.GetOrLoadAsync("k", _ => Task.FromResult<string?>("fresh"));
        release.SetResult();
        await slow;

        Assert.Equal("fresh", fresh);
        Assert.Equal("fresh", await cache.GetOrLoadAsync("k", _ => Task.FromResult<string?>("not called"))); // the stale load was not stored over it
    }
}
