/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace WebCore.Caching;

/// <summary>One repository method and how many times real storage was called through it.</summary>
public record StorageCallCount(string Repository, string Method, long Calls);

/// <summary>
/// Counts calls that reach real storage. Repositories are wrapped (<see cref="Wrap{T}"/>) UNDER their caches, so a call here means a cache miss or an uncached
/// repository - exactly what a page's read budget is measured in. The totals are on the status endpoint (calls per request is the number to watch), and the
/// read-budget tests reset and read them around a page load.
/// </summary>
public static class StorageCalls
{
    private static readonly ConcurrentDictionary<(string Repository, string Method), long> Counts = new();

    /// <summary>Wraps a repository so every call to it is counted. The wrapper implements the same interface and forwards everything.</summary>
    public static T Wrap<T>(T inner) where T : class
    {
        var proxy = DispatchProxy.Create<T, CountingProxy<T>>();
        var counting = (CountingProxy<T>)(object)proxy;
        counting.Inner = inner;
        counting.Repository = typeof(T).Name;
        return proxy;
    }

    public static long Total => Counts.Values.Sum();

    public static IReadOnlyList<StorageCallCount> Snapshot() =>
        Counts.Select(c => new StorageCallCount(c.Key.Repository, c.Key.Method, c.Value))
            .OrderByDescending(c => c.Calls)
            .ToList();

    /// <summary>For tests: start counting from zero.</summary>
    public static void Reset() => Counts.Clear();

    internal static void Record(string repository, string method) => Counts.AddOrUpdate((repository, method), 1, (_, n) => n + 1);

    public class CountingProxy<T> : DispatchProxy where T : class
    {
        public T Inner { get; set; } = null!;

        public string Repository { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Record(Repository, targetMethod!.Name);
            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
