/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Storage;

namespace WebCore.Health;

/// <summary>A signal that changed level when it was published: the thing alerts are made from.</summary>
public sealed record HealthTransition(HealthSignal Signal, HealthStatus Previous);

/// <summary>
/// Keeps each environment's signals and a short history of each, in small documents (one per environment, so staging and production never show each other's results even when they share storage). Publishing replaces
/// a signal by id, so a tool only ever sends what it measured; what changed level comes back so the caller can alert. Reads are cheap and rare (an admin page), so nothing is cached.
/// </summary>
public sealed class HealthSignalStore(IKeyedDocumentStore documents) : IDisposable
{
    /// <summary>Points of history kept per signal.</summary>
    public const int MaxHistoryPerSignal = 90;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<HealthSignal>> GetAsync(string environment, CancellationToken cancellationToken = default)
    {
        var signals = await documents.GetAsync<Dictionary<string, HealthSignal>>(Name(environment, "signals"), cancellationToken).ConfigureAwait(false);
        return (signals?.Values ?? Enumerable.Empty<HealthSignal>()).OrderBy(s => s.Area, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyDictionary<string, List<HealthPoint>>> GetHistoryAsync(string environment, CancellationToken cancellationToken = default) =>
        await documents.GetAsync<Dictionary<string, List<HealthPoint>>>(Name(environment, "history"), cancellationToken).ConfigureAwait(false) ?? new Dictionary<string, List<HealthPoint>>();

    /// <summary>Stores the signals (replacing any with the same id), adds a history point for each, and returns the ones whose level changed.</summary>
    public async Task<IReadOnlyList<HealthTransition>> PublishAsync(string environment, IEnumerable<HealthSignal> incoming, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var signals = await documents.GetAsync<Dictionary<string, HealthSignal>>(Name(environment, "signals"), cancellationToken).ConfigureAwait(false) ?? new Dictionary<string, HealthSignal>();
            var history = await documents.GetAsync<Dictionary<string, List<HealthPoint>>>(Name(environment, "history"), cancellationToken).ConfigureAwait(false) ?? new Dictionary<string, List<HealthPoint>>();
            var changed = new List<HealthTransition>();
            foreach (var signal in incoming)
            {
                var previous = signals.TryGetValue(signal.Id, out var before) ? before.Status : HealthStatus.Unknown;
                if (previous != signal.Status)
                {
                    changed.Add(new HealthTransition(signal, previous));
                }

                signals[signal.Id] = signal;
                AddPoint(history, signal);
            }

            await documents.SetAsync(Name(environment, "signals"), signals, cancellationToken).ConfigureAwait(false);
            await documents.SetAsync(Name(environment, "history"), history, cancellationToken).ConfigureAwait(false);
            return changed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The ids that have already been alerted as stale, so a signal that stays stale is announced once.</summary>
    public async Task<ISet<string>> GetStaleAlertedAsync(string environment, CancellationToken cancellationToken = default) =>
        (await documents.GetAsync<HashSet<string>>(Name(environment, "stale-alerted"), cancellationToken).ConfigureAwait(false)) ?? [];

    public Task SetStaleAlertedAsync(string environment, ISet<string> ids, CancellationToken cancellationToken = default) =>
        documents.SetAsync(Name(environment, "stale-alerted"), ids.ToHashSet(StringComparer.Ordinal), cancellationToken);

    /// <summary>The environment names a store will accept: letters, digits, dashes and underscores, lower-cased, so it is safe in a document name.</summary>
    public static string NormalizeEnvironment(string? environment) =>
        DocumentNames.IsValid(environment) ? environment!.ToLowerInvariant() : throw new ArgumentException($"'{environment}' is not a valid environment name.", nameof(environment));

    /// <summary>Releases the lock that serializes publishing.</summary>
    public void Dispose() => _gate.Dispose();

    private static void AddPoint(Dictionary<string, List<HealthPoint>> history, HealthSignal signal)
    {
        if (!history.TryGetValue(signal.Id, out var points))
        {
            points = [];
            history[signal.Id] = points;
        }

        if (points.Count > 0 && points[^1].At == signal.AsOf)
        {
            return;
        }

        points.Add(new HealthPoint(signal.AsOf, signal.Value, signal.Status));
        if (points.Count > MaxHistoryPerSignal)
        {
            points.RemoveRange(0, points.Count - MaxHistoryPerSignal);
        }
    }

    private static string Name(string environment, string kind) => $"health-{NormalizeEnvironment(environment)}-{kind}.json";
}
