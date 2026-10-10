/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Storage;

/// <summary>
/// How the storage layer reaches into a record without the record knowing it is stored: where its id lives, and (when it keeps one) its last-saved time. The repository for a record supplies this;
/// the record stays a plain domain type.
/// </summary>
/// <param name="IdOf">Reads the record's id.</param>
/// <param name="SetId">Gives the record an id (called when it has none).</param>
/// <param name="Stamp">Records the time of a save on the record; null for a record that does not keep one.</param>
/// <param name="LenientIds">True when an id that cannot be real (it comes from a URL or a form) means "no such record" on a read rather than an error.</param>
public sealed record DocumentShape<T>(Func<T, string> IdOf, Action<T, string> SetId, Action<T, DateTimeOffset>? Stamp = null, bool LenientIds = false);

/// <summary>
/// The get / save / delete / list-everything that every id-keyed repository needs, written once over an <see cref="IDocumentStore"/>. A concrete repository adds only what is particular to its record: how
/// it filters and orders a list, and any extra operations.
/// </summary>
public abstract class DocumentRepository<T>(IDocumentStore store, string label, DocumentShape<T> shape, TimeProvider? timeProvider = null) where T : class
{
    public Task<T?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        shape.LenientIds && !DocumentNames.IsValid(id)
            ? Task.FromResult<T?>(null)
            : store.GetAsync<T>(DocumentNameFor(id), cancellationToken);

    public async Task<T> SaveAsync(T document, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shape.IdOf(document)))
        {
            shape.SetId(document, Guid.NewGuid().ToString("N"));
        }

        shape.Stamp?.Invoke(document, (timeProvider ?? TimeProvider.System).GetUtcNow());

        await store.SetAsync(DocumentNameFor(shape.IdOf(document)), document, cancellationToken).ConfigureAwait(false);
        return document;
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        store.DeleteAsync(DocumentNameFor(id), cancellationToken);

    /// <summary>Every readable document, unordered; a corrupt one is skipped.</summary>
    protected async Task<List<T>> ListAllAsync(CancellationToken cancellationToken)
    {
        var all = new List<T>();
        await foreach (var (_, document) in store.ListAsync<T>(cancellationToken).ConfigureAwait(false))
        {
            all.Add(document);
        }

        return all;
    }

    private string DocumentNameFor(string id) => DocumentNames.For(id, label);
}
