/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Collections.Concurrent;

namespace WebCore.Caching;

/// <summary>Hit, miss and invalidation counts for one named cache.</summary>
public sealed class CacheCounters
{
    private long _hits;
    private long _misses;
    private long _invalidations;

    public CacheCounters(string name) => Name = name;

    public string Name { get; }

    public long Hits => Interlocked.Read(ref _hits);

    public long Misses => Interlocked.Read(ref _misses);

    public long Invalidations => Interlocked.Read(ref _invalidations);

    public void Hit() => Interlocked.Increment(ref _hits);

    public void Miss() => Interlocked.Increment(ref _misses);

    public void Invalidated() => Interlocked.Increment(ref _invalidations);
}

/// <summary>One cache's numbers as reported by the status endpoint and shown on the release-notes page.</summary>
public record CacheSnapshot(string Name, long Hits, long Misses, long Invalidations)
{
    /// <summary>Share of reads served from memory, or null before anything has been read.</summary>
    public double? HitRatio => Hits + Misses == 0 ? null : (double)Hits / (Hits + Misses);
}

/// <summary>
/// Every cache built from the shared building blocks registers its counters here by name, so "is this cache actually working?" is a number you can look at
/// (a cache that never hits is dead weight; one that hits 99% is doing its job) - see docs/performance.md.
/// </summary>
public static class CacheRegistry
{
    private static readonly ConcurrentDictionary<string, CacheCounters> Counters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The counters for <paramref name="name"/>; caches sharing a name (several instances of one kind) share the numbers.</summary>
    public static CacheCounters For(string name) => Counters.GetOrAdd(name, n => new CacheCounters(n));

    public static IReadOnlyList<CacheSnapshot> Snapshot() =>
        Counters.Values
            .Select(c => new CacheSnapshot(c.Name, c.Hits, c.Misses, c.Invalidations))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
