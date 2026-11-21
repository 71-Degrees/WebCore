/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using WebCore.Caching;

namespace WebCore.Tests.Adversarial;

// Caches, counters and the storage-call proxy under zero and negative lifetimes, null keys and loaders,
// disposal, and a proxy over an interface that throws.
public sealed class CachingAdversarialTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    // ---- KeyedCache ----

    [Fact]
    public void KeyedCache_ZeroTimeToLive_NeverServesAnEntry()
    {
        var cache = new KeyedCache<string, string>("adv.keyed.zero", TimeSpan.Zero, () => Start);
        cache.Set("k", "v", cache.Generation);

        Assert.False(cache.TryGet("k", out _));
    }

    [Fact]
    public void KeyedCache_NegativeTimeToLive_NeverServesAnEntry()
    {
        var cache = new KeyedCache<string, string>("adv.keyed.negative", TimeSpan.FromMinutes(-1), () => Start);
        cache.Set("k", "v", cache.Generation);

        Assert.False(cache.TryGet("k", out _));
    }

    [Fact]
    public void KeyedCache_ZeroMaximumEntries_HoldsExactlyOneEntry_NotNone()
    {
        // The size check keeps at least one entry (Math.Max(1, max/10)), so a limit of zero behaves as one.
        var cache = new KeyedCache<string, string>("adv.keyed.max0", TimeSpan.FromMinutes(5), () => Start, maxEntries: 0);
        cache.Set("first", "1", cache.Generation);
        cache.Set("second", "2", cache.Generation);

        Assert.False(cache.TryGet("first", out _));
        Assert.True(cache.TryGet("second", out var second));
        Assert.Equal("2", second);
    }

    [Fact]
    public void KeyedCache_SetWithAStaleGeneration_IsDropped()
    {
        var cache = new KeyedCache<string, string>("adv.keyed.stale", TimeSpan.FromMinutes(5), () => Start);
        var before = cache.Generation;
        cache.Invalidate();

        cache.Set("k", "v", before);

        Assert.False(cache.TryGet("k", out _));
    }

    [Fact]
    public async Task KeyedCache_NullKey_ThrowsArgumentNullBeforeLoading()
    {
        var cache = new KeyedCache<string, string>("adv.keyed.nullkey", TimeSpan.FromMinutes(5), () => Start);
        var loaded = false;

        await Assert.ThrowsAsync<ArgumentNullException>(() => cache.GetOrLoadAsync(null!, _ =>
        {
            loaded = true;
            return Task.FromResult<string?>("x");
        }));
        Assert.False(loaded);
    }

    [Fact]
    public async Task KeyedCache_LoaderThatReturnsNull_IsCalledAgainOnTheNextRead()
    {
        var cache = new KeyedCache<string, string>("adv.keyed.nullload", TimeSpan.FromMinutes(5), () => Start);
        var calls = 0;

        await cache.GetOrLoadAsync("k", _ => { calls++; return Task.FromResult<string?>(null); });
        await cache.GetOrLoadAsync("k", _ => { calls++; return Task.FromResult<string?>(null); });

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task KeyedCache_AlreadyCancelledToken_StillReturnsACompletedLoad_AsRecorded()
    {
        // Checked: when the load has already completed, WaitAsync hands back the result even for a cancelled token.
        var cache = new KeyedCache<string, string>("adv.keyed.cancelled", TimeSpan.FromMinutes(5), () => Start);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var value = await cache.GetOrLoadAsync("k", _ => Task.FromResult<string?>("loaded"), cts.Token);

        Assert.Equal("loaded", value);
        Assert.True(cache.TryGet("k", out _));
    }

    [Fact]
    public void KeyedCache_InvalidateAKeyThatWasNeverSet_IsHarmless()
    {
        var cache = new KeyedCache<string, string>("adv.keyed.invalidate", TimeSpan.FromMinutes(5), () => Start);

        cache.Invalidate("never-there");

        Assert.False(cache.TryGet("never-there", out _));
    }

    // ---- SnapshotCache ----

    [Fact]
    public async Task SnapshotCache_LoaderReturningNull_IsNotServedAsFresh()
    {
        var cache = new SnapshotCache<string>("adv.snapshot.null", TimeSpan.FromMinutes(5), () => Start);
        var calls = 0;

        await cache.GetAsync(_ => { calls++; return Task.FromResult<string>(null!); });
        await cache.GetAsync(_ => { calls++; return Task.FromResult<string>(null!); });

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SnapshotCache_ZeroOrNegativeLifetime_LoadsOnEveryCall()
    {
        var zero = new SnapshotCache<string>("adv.snapshot.zero", TimeSpan.Zero, () => Start);
        var negative = new SnapshotCache<string>("adv.snapshot.negative", TimeSpan.FromSeconds(-1), () => Start);
        var calls = 0;

        for (var i = 0; i < 3; i++)
        {
            await zero.GetAsync(_ => { calls++; return Task.FromResult("z"); });
            await negative.GetAsync(_ => { calls++; return Task.FromResult("n"); });
        }

        Assert.Equal(6, calls);
    }

    [Fact]
    public async Task SnapshotCache_NullLoader_ThrowsNullReferenceWhenItNeedsToLoad()
    {
        var cache = new SnapshotCache<string>("adv.snapshot.noloader", TimeSpan.FromMinutes(5), () => Start);

        await Assert.ThrowsAsync<NullReferenceException>(() => cache.GetAsync(null!));
    }

    [Fact]
    public async Task SnapshotCache_UseAfterDispose_IsAnObjectDisposedErrorOnlyWhenItMustLoad()
    {
        var cache = new SnapshotCache<string>("adv.snapshot.disposed", TimeSpan.FromMinutes(5), () => Start);
        await cache.GetAsync(_ => Task.FromResult("fresh"));
        cache.Dispose();

        // A fresh snapshot is still served without touching the disposed lock; a stale one must load and so fails.
        Assert.Equal("fresh", await cache.GetAsync(_ => Task.FromResult("never")));
        cache.Invalidate();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.GetAsync(_ => Task.FromResult("never")));
    }

    // ---- CacheRegistry and counters ----

    [Fact]
    public void CacheRegistry_NamesAreCaseInsensitive_SoTwoCachesSharingACaseShareCounters()
    {
        Assert.Same(CacheRegistry.For("Adv.Name.Case"), CacheRegistry.For("adv.name.case"));
    }

    [Fact]
    public void CacheRegistry_NullName_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => CacheRegistry.For(null!));
    }

    [Fact]
    public void CacheSnapshot_HitRatio_IsNullWithoutReadsAndExactOtherwise()
    {
        Assert.Null(new CacheSnapshot("x", 0, 0, 0).HitRatio);
        Assert.Equal(0.75, new CacheSnapshot("x", 3, 1, 0).HitRatio);
        Assert.Equal(0.0, new CacheSnapshot("x", 0, 5, 0).HitRatio);
    }

    // ---- StaticAssetCachePolicy ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("noextension")]
    public void StaticAssetCachePolicy_NoUsableExtension_IsNotCachedLongOrHashed(string? fileName)
    {
        Assert.Null(StaticAssetCachePolicy.For(fileName!, addressCarriesContentHash: true));
    }

    [Theory]
    [InlineData("site.CSS", true, StaticAssetCachePolicy.Immutable)]
    [InlineData("site.Js", true, StaticAssetCachePolicy.Immutable)]
    [InlineData("site.css", false, null)]
    [InlineData("pic.PNG", false, StaticAssetCachePolicy.Month)]
    [InlineData("a.b.css", true, StaticAssetCachePolicy.Immutable)]
    [InlineData("page.html", true, null)]
    [InlineData("data.json", true, null)]
    [InlineData("clip.mp4", true, null)]
    public void StaticAssetCachePolicy_ExtensionsAreCaseInsensitiveAndHashAware(string fileName, bool hashed, string? expected)
    {
        Assert.Equal(expected, StaticAssetCachePolicy.For(fileName, hashed));
    }

    [Fact]
    public void StaticAssetCachePolicy_IsDecided_ReflectsEveryListedAndExemptType()
    {
        Assert.True(StaticAssetCachePolicy.IsDecided("a.html"));
        Assert.True(StaticAssetCachePolicy.IsDecided("a.WOFF2"));
        Assert.False(StaticAssetCachePolicy.IsDecided("a.mp4"));
        Assert.False(StaticAssetCachePolicy.IsDecided("noextension"));
    }

    [Fact]
    public void StaticAssetCachePolicy_ExemptTable_IsMutableBehindItsReadOnlyType_Finding()
    {
        // The public table is typed IReadOnlyDictionary but backed by a mutable Dictionary. Only the flag is checked:
        // writing through a cast would change a process-wide policy that other test classes read.
        Assert.False(((IDictionary<string, string>)StaticAssetCachePolicy.Exempt).IsReadOnly);
    }

    // ---- StorageCalls proxy ----

    public interface IProbeRepository
    {
        int Count(int x);

        int Explode();

        string? Echo(string? text);
    }

    private sealed class ProbeRepository : IProbeRepository
    {
        public int Count(int x) => x;

        public int Explode() => throw new InvalidOperationException("boom");

        public string? Echo(string? text) => text;
    }

    [Fact]
    public void StorageCalls_Wrap_OfAClassNotAnInterface_IsRefused()
    {
        Assert.ThrowsAny<ArgumentException>(() => StorageCalls.Wrap(new ProbeRepository()));
    }

    [Fact]
    public void StorageCalls_Wrap_RethrowsTheInnerExceptionTypeNotTheReflectionWrapper()
    {
        var proxy = StorageCalls.Wrap<IProbeRepository>(new ProbeRepository());

        Assert.Throws<InvalidOperationException>(() => proxy.Explode());
    }

    [Fact]
    public void StorageCalls_Wrap_PassesNullArgumentsAndNullReturnsThrough()
    {
        var proxy = StorageCalls.Wrap<IProbeRepository>(new ProbeRepository());

        Assert.Null(proxy.Echo(null));
        Assert.Equal(int.MinValue, proxy.Count(int.MinValue));
        Assert.Equal(int.MaxValue, proxy.Count(int.MaxValue));
    }

    [Fact]
    public void StorageCalls_Wrap_WithANullInner_FailsOnTheFirstCall()
    {
        var proxy = StorageCalls.Wrap<IProbeRepository>(null!);

        Assert.ThrowsAny<Exception>(() => proxy.Count(1));
    }

    // StorageCalls is process-wide and shared with other test classes, so this test reads its own delta and never resets.
    [Fact]
    public void StorageCalls_CountsByRepositoryAndMethod()
    {
        var proxy = StorageCalls.Wrap<IProbeRepository>(new ProbeRepository());
        var before = CountFor("IProbeRepository", "Count");

        proxy.Count(1);
        proxy.Count(2);

        Assert.Equal(before + 2, CountFor("IProbeRepository", "Count"));
    }

    private static long CountFor(string repository, string method) =>
        StorageCalls.Snapshot()
            .FirstOrDefault(c => c.Repository == repository && c.Method == method)?.Calls ?? 0;
}
