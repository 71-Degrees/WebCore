/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Health;

namespace WebCore.Hosting.Health;

/// <summary>The environment this running copy of the site is: what it reads and publishes signals for ("production", "staging").</summary>
public sealed record HealthEnvironment(string Name);

/// <summary>
/// Notices the silence. A nightly job that stops reporting raises no event of its own, so something has to look: every quarter hour this reads the signals, and any that are past their expected time are announced
/// once (not every pass) and announced again only after they have reported and gone quiet again.
/// </summary>
public sealed class HealthWatchdog(HealthSignalStore store, HealthAlertDispatcher alerts, HealthEnvironment environment, TimeProvider time, ILogger<HealthWatchdog> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>One pass; public so a test can drive it. Returns how many stale signals were newly announced.</summary>
    public async Task<int> CheckOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var stale = (await store.GetAsync(environment.Name, cancellationToken).ConfigureAwait(false)).Where(s => s.IsStale(now)).ToList();
        var alerted = await store.GetStaleAlertedAsync(environment.Name, cancellationToken).ConfigureAwait(false);
        var fresh = stale.Where(s => !alerted.Contains(s.Id)).ToList();
        if (fresh.Count > 0)
        {
            await alerts.DispatchAsync(fresh.Select(s => HealthAlertPolicy.ForStale(environment.Name, s, now)), cancellationToken).ConfigureAwait(false);
        }

        var staleIds = stale.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        if (!staleIds.SetEquals(alerted))
        {
            await store.SetStaleAlertedAsync(environment.Name, staleIds, cancellationToken).ConfigureAwait(false);
        }

        return fresh.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            await PassAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    // The top of this code path: a failed pass is logged and the next one still runs.
    private async Task PassAsync(CancellationToken stoppingToken)
    {
        try
        {
            await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "The health watchdog's pass failed; it will try again in {Minutes} minutes.", Interval.TotalMinutes);
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
