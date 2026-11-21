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
/// that name one), <c>actor-{id}.json</c> per actor (the item histories that actor wrote into, so forgetting them can find those) and <c>recent.json</c> across
/// everything, each capped and with entries older than <see cref="AuditLogOptions.KeepFor"/> dropped as it is written.
/// Appends take turns inside this process; a second instance appending at the same moment could drop one entry, which is accepted for a trail.
/// </summary>
public sealed partial class AuditLog(IKeyedDocumentStore store, TimeProvider time, AuditLogOptions? options = null) : IAuditLog, IDisposable
{
    private const string RecentName = "recent.json";

    private readonly AuditLogOptions _options = Validated(options ?? new AuditLogOptions());
    private readonly SemaphoreSlim _turn = new(1, 1);

    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var item = ItemName(entry.Kind, entry.EntityId);
            await AddAsync(item, entry, _options.KeepPerItem, cancellationToken).ConfigureAwait(false);
            await IndexActorAsync(entry, item, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Removes the owner's own list, every entry the owner owns (from their items' histories and the recent list), and anonymises the owner's actions on other
    /// owners' items (the actor becomes <see cref="AuditActor.ForgottenId"/>, keeping the name). Entries written before the actor index existed are found only
    /// through the owner's own items and the recent list; the rest age out with <see cref="AuditLogOptions.KeepFor"/>.
    /// </summary>
    public async Task<int> ForgetOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mine = await LoadAsync(OwnerName(ownerId), cancellationToken).ConfigureAwait(false);
            await store.DeleteAsync(OwnerName(ownerId), cancellationToken).ConfigureAwait(false);
            var removed = mine.Count;
            // The histories of the owner's own items, and of the items the person wrote into about other owners, are visited; so is the recent list.
            var items = mine.Select(e => ItemName(e.Kind, e.EntityId)).Concat(await IndexedItemsAsync(ownerId, cancellationToken).ConfigureAwait(false));
            foreach (var item in items.Distinct(StringComparer.Ordinal))
            {
                removed += await ForgetInAsync(item, ownerId, cancellationToken).ConfigureAwait(false);
            }

            removed += await ForgetInAsync(RecentName, ownerId, cancellationToken).ConfigureAwait(false);
            await store.DeleteAsync(ActorName(ownerId), cancellationToken).ConfigureAwait(false);
            return removed;
        }
        finally
        {
            _turn.Release();
        }
    }

    public void Dispose() => _turn.Dispose();

    // Refused at construction: a negative keep would otherwise fail on the first append, long after the bad setting was made.
    // A keep of zero is allowed and means "keep none of that list".
    private static AuditLogOptions Validated(AuditLogOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(options.KeepPerItem, nameof(AuditLogOptions.KeepPerItem));
        ArgumentOutOfRangeException.ThrowIfNegative(options.KeepPerOwner, nameof(AuditLogOptions.KeepPerOwner));
        ArgumentOutOfRangeException.ThrowIfNegative(options.KeepRecent, nameof(AuditLogOptions.KeepRecent));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.KeepFor, TimeSpan.Zero, nameof(AuditLogOptions.KeepFor));

        return options;
    }

    // Entries the owner owns are removed. Entries where the owner was only the actor stay, with the actor anonymised, so the other owner's history keeps its
    // shape. Returns how many stored copies were removed or anonymised; nothing is written when there were none.
    private async Task<int> ForgetInAsync(string name, string ownerId, CancellationToken cancellationToken)
    {
        var all = await LoadAsync(name, cancellationToken).ConfigureAwait(false);
        var touched = all.Count(e => e.OwnerId == ownerId || e.Actor.Id == ownerId);
        if (touched == 0)
        {
            return 0;
        }

        var kept = all.Where(e => e.OwnerId != ownerId).Select(e => Anonymised(e, ownerId)).ToList();
        await store.SetAsync(name, kept, cancellationToken).ConfigureAwait(false);
        return touched;
    }

    private static AuditEntry Anonymised(AuditEntry entry, string ownerId) =>
        entry.Actor.Id == ownerId ? entry with { Actor = AuditActor.Forgotten(entry.Actor) } : entry;

    // An actor's own index of the item histories they wrote into. Entries the person owns are not indexed: they go with the owner's own list. An actor id that
    // is not a valid document id has no document name, so it is not indexed; that must never fail the append.
    private async Task IndexActorAsync(AuditEntry entry, string itemName, CancellationToken cancellationToken)
    {
        if (!IsIndexedActor(entry))
        {
            return;
        }

        var name = ActorName(entry.Actor.Id);
        var items = await store.GetAsync<List<string>>(name, cancellationToken).ConfigureAwait(false) ?? [];
        if (items.Count > 0 && items[^1] == itemName)
        {
            return;
        }

        items.Remove(itemName);
        items.Add(itemName);
        await store.SetAsync(name, Capped(items, _options.KeepPerOwner), cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> IndexedItemsAsync(string actorId, CancellationToken cancellationToken) =>
        await store.GetAsync<List<string>>(ActorName(actorId), cancellationToken).ConfigureAwait(false) ?? [];

    private static bool IsIndexedActor(AuditEntry entry) =>
        DocumentNames.IsValid(entry.Actor.Id)
        && entry.Actor.Id != AuditActor.System.Id
        && entry.Actor.Id != AuditActor.ForgottenId
        && entry.Actor.Id != entry.OwnerId;

    private static string ItemName(string kind, string entityId)
    {
        if (!KindPattern().IsMatch(kind))
        {
            throw new ArgumentException($"'{kind}' is not a valid audit kind (lower case letters, digits and dashes).", nameof(kind));
        }

        return $"item-{kind}-{DocumentNames.For(entityId, "audited item")}";
    }

    private static string OwnerName(string ownerId) => "owner-" + DocumentNames.For(ownerId, "owner");

    private static string ActorName(string actorId) => "actor-" + DocumentNames.For(actorId, "actor");

    private async Task AddAsync(string name, AuditEntry entry, int keep, CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow() - _options.KeepFor;
        var all = (await LoadAsync(name, cancellationToken).ConfigureAwait(false)).Where(e => e.At >= cutoff).ToList();
        all.Add(entry);
        await store.SetAsync(name, Capped(all, keep), cancellationToken).ConfigureAwait(false);
    }

    // The newest <paramref name="keep"/> of a list stored newest last.
    private static List<T> Capped<T>(List<T> items, int keep) => items.Count > keep ? items[^keep..] : items;

    private async Task<List<AuditEntry>> LoadAsync(string name, CancellationToken cancellationToken) =>
        await store.GetAsync<List<AuditEntry>>(name, cancellationToken).ConfigureAwait(false) ?? [];

    private async Task<IReadOnlyList<AuditEntry>> ReadAsync(string name, int count, CancellationToken cancellationToken) =>
        [.. (await LoadAsync(name, cancellationToken).ConfigureAwait(false)).AsEnumerable().Reverse().Take(count)];

    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex KindPattern();
}
