/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Collections.Concurrent;

namespace WebCore.Caching;

/// <summary>Implemented by a cache that can say how many entries it holds right now (read by the registry, never by callers).</summary>
internal interface ICountsEntries
{
    long EntryCount { get; }
}

/// <summary>
/// What a custom entry reports to the registry: how many entries it holds and a short line for the admin page (for example "3 rebuilds").
/// </summary>
public readonly record struct CacheReport(long Entries, string? Detail = null);

/// <summary>Hit, miss and invalidation counts for one named cache, and the entries held by the caches that share its name.</summary>
public sealed class CacheCounters
{
    // Weak, because the registry is static and lives as long as the app: a strong reference would keep a short-lived cache (a test's, a replaced one) alive forever.
    private readonly object _sourcesGate = new();
    private readonly List<WeakReference<ICountsEntries>> _sources = new();
    private long _hits;
    private long _misses;
    private long _invalidations;

    // Held here rather than in a second static dictionary in the registry: the registry's one static map already keys every name, and a second one is hidden shared state.
    private volatile Func<CacheReport>? _report;

    public CacheCounters(string name) => Name = name;

    public string Name { get; }

    public long Hits => Interlocked.Read(ref _hits);

    public long Misses => Interlocked.Read(ref _misses);

    public long Invalidations => Interlocked.Read(ref _invalidations);

    /// <summary>Entries across the live caches tracked under this name, or null when none is tracked (a name used only for counters).</summary>
    public long? Entries
    {
        get
        {
            lock (_sourcesGate)
            {
                long total = 0;
                var live = 0;
                for (var i = _sources.Count - 1; i >= 0; i--)
                {
                    if (_sources[i].TryGetTarget(out var source))
                    {
                        total += source.EntryCount;
                        live++;
                    }
                    else
                    {
                        _sources.RemoveAt(i);
                    }
                }

                return live == 0 ? null : total;
            }
        }
    }

    /// <summary>The custom entry registered under this name, or null when there is none.</summary>
    internal CacheReport? Report => _report?.Invoke();

    public void Hit() => Interlocked.Increment(ref _hits);

    public void Miss() => Interlocked.Increment(ref _misses);

    public void Invalidated() => Interlocked.Increment(ref _invalidations);

    internal void SetReport(Func<CacheReport> report) => _report = report;

    internal void Track(ICountsEntries source)
    {
        lock (_sourcesGate)
        {
            _sources.RemoveAll(reference => !reference.TryGetTarget(out _));
            _sources.Add(new WeakReference<ICountsEntries>(source));
        }
    }
}

/// <summary>
/// One cache's numbers as reported by the status endpoint and shown on the release-notes page. Entries and Detail were added later; they are optional
/// and trailing so older callers and readers of the /status JSON keep working (the new fields are additive).
/// </summary>
public record CacheSnapshot(string Name, long Hits, long Misses, long Invalidations, long? Entries = null, string? Detail = null)
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

    /// <summary>
    /// Adds a custom entry under <paramref name="name"/> for an in-memory structure that is not one of the caches above (an app's merged catalog index).
    /// For a long-lived (singleton) component: the delegate runs on every status read, so it must be cheap and must not throw. Registering a name again
    /// replaces the earlier delegate, since an app host built twice in one process (as tests do) keeps only the newest.
    /// </summary>
    public static void Report(string name, Func<CacheReport> report)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(report);
        For(name).SetReport(report);
    }

    public static IReadOnlyList<CacheSnapshot> Snapshot() =>
        Counters.Values
            .Select(Row)
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // A custom entry's own numbers win over the caches' entry count, so a name registered both ways shows one row with the report's figures.
    private static CacheSnapshot Row(CacheCounters counters)
    {
        long? entries = counters.Entries;
        string? detail = null;
        if (counters.Report is { } reported)
        {
            entries = reported.Entries;
            detail = reported.Detail;
        }

        return new CacheSnapshot(counters.Name, counters.Hits, counters.Misses, counters.Invalidations, entries, detail);
    }
}
