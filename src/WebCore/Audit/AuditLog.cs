/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using System.Text.RegularExpressions;
using WebCore.Storage;

namespace WebCore.Audit;

/// <summary>How much of the trail is kept: entries per item, per owner and across everything, and how long any entry lives.</summary>
public sealed record AuditLogOptions
{
    public int KeepPerItem { get; init; } = 200;

    public int KeepPerOwner { get; init; } = 500;

    public int KeepRecent { get; init; } = 1000;

    public TimeSpan KeepFor { get; init; } = TimeSpan.FromDays(180);
}

/// <summary>
/// The trail in one keyed document store (the app's <c>audit</c> store): <c>item-{kind}-{id}.json</c> per item, <c>owner-{id}.json</c> per owner (only for entries
/// that name one) and <c>recent.json</c> across everything, each capped and with entries older than <see cref="AuditLogOptions.KeepFor"/> dropped as it is written.
/// Appends take turns inside this process; a second instance appending at the same moment could drop one entry, which is accepted for a trail.
/// </summary>
public sealed partial class AuditLog(IKeyedDocumentStore store, TimeProvider time, AuditLogOptions? options = null) : IAuditLog, IDisposable
{
    private const string RecentName = "recent.json";

    private readonly AuditLogOptions _options = options ?? new AuditLogOptions();
    private readonly SemaphoreSlim _turn = new(1, 1);

    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AddAsync(ItemName(entry.Kind, entry.EntityId), entry, _options.KeepPerItem, cancellationToken).ConfigureAwait(false);
            if (entry.OwnerId is { Length: > 0 } owner)
            {
                await AddAsync(OwnerName(owner), entry, _options.KeepPerOwner, cancellationToken).ConfigureAwait(false);
            }

            await AddAsync(RecentName, entry, _options.KeepRecent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _turn.Release();
        }
    }

    public Task<IReadOnlyList<AuditEntry>> ForItemAsync(string kind, string entityId, int count, CancellationToken cancellationToken = default) =>
        ReadAsync(ItemName(kind, entityId), count, cancellationToken);

    public Task<IReadOnlyList<AuditEntry>> ForOwnerAsync(string ownerId, int count, CancellationToken cancellationToken = default) =>
        ReadAsync(OwnerName(ownerId), count, cancellationToken);

    public Task<IReadOnlyList<AuditEntry>> RecentAsync(int count, CancellationToken cancellationToken = default) => ReadAsync(RecentName, count, cancellationToken);

    public async Task<int> ForgetOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mine = await LoadAsync(OwnerName(ownerId), cancellationToken).ConfigureAwait(false);
            await store.DeleteAsync(OwnerName(ownerId), cancellationToken).ConfigureAwait(false);
            var removed = mine.Count;
            // The owner's items' own histories, and the shared recent list, lose their entries too.
            foreach (var item in mine.Select(e => ItemName(e.Kind, e.EntityId)).Distinct(StringComparer.Ordinal))
            {
                removed += await RemoveOwnerFromAsync(item, ownerId, cancellationToken).ConfigureAwait(false);
            }

            return removed + await RemoveOwnerFromAsync(RecentName, ownerId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _turn.Release();
        }
    }

    public void Dispose() => _turn.Dispose();

    private static bool Concerns(AuditEntry entry, string ownerId) => entry.OwnerId == ownerId || entry.Actor.Id == ownerId;

    private async Task<int> RemoveOwnerFromAsync(string name, string ownerId, CancellationToken cancellationToken)
    {
        var all = await LoadAsync(name, cancellationToken).ConfigureAwait(false);
        var kept = all.Where(e => !Concerns(e, ownerId)).ToList();
        if (kept.Count == all.Count)
        {
            return 0;
        }

        await store.SetAsync(name, kept, cancellationToken).ConfigureAwait(false);
        return all.Count - kept.Count;
    }

    private static string ItemName(string kind, string entityId)
    {
        if (!KindPattern().IsMatch(kind))
        {
            throw new ArgumentException($"'{kind}' is not a valid audit kind (lower case letters, digits and dashes).", nameof(kind));
        }

        return $"item-{kind}-{DocumentNames.For(entityId, "audited item")}";
    }

    private static string OwnerName(string ownerId) => "owner-" + DocumentNames.For(ownerId, "owner");

    private async Task AddAsync(string name, AuditEntry entry, int keep, CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow() - _options.KeepFor;
        var all = (await LoadAsync(name, cancellationToken).ConfigureAwait(false)).Where(e => e.At >= cutoff).ToList();
        all.Add(entry);
        await store.SetAsync(name, all.Count > keep ? all[^keep..] : all, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<AuditEntry>> LoadAsync(string name, CancellationToken cancellationToken) =>
        await store.GetAsync<List<AuditEntry>>(name, cancellationToken).ConfigureAwait(false) ?? [];

    private async Task<IReadOnlyList<AuditEntry>> ReadAsync(string name, int count, CancellationToken cancellationToken) =>
        [.. (await LoadAsync(name, cancellationToken).ConfigureAwait(false)).AsEnumerable().Reverse().Take(count)];

    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex KindPattern();
}
