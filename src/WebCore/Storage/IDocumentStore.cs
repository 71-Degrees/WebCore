/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Storage;

/// <summary>
/// Keeping documents one at a time, by name: read, write, create-if-absent and delete. This is all a store of "one record per key" (a user's connection, a month's spend, an opt-out) needs, so
/// those depend on this and not on the ability to list everything. A name is the document's file or blob name, extension included (<c>abc123.json</c>).
/// </summary>
public interface IKeyedDocumentStore
{
    /// <summary>The document, or null when it does not exist or cannot be read (a corrupt document is treated as absent, never thrown).</summary>
    Task<T?> GetAsync<T>(string name, CancellationToken cancellationToken = default);

    /// <summary>True when a document exists under this name, readable or not.</summary>
    Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Writes the document, replacing any existing one. A reader never sees a half-written document.</summary>
    Task SetAsync<T>(string name, T value, CancellationToken cancellationToken = default);

    /// <summary>Writes the document only if none exists under this name; false (and nothing written) when one already does. Atomic, so two callers racing to create the same document cannot both win.</summary>
    Task<bool> TrySetIfNotExistsAsync<T>(string name, T value, CancellationToken cancellationToken = default);

    /// <summary>Removes the document; false when there was none.</summary>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>
/// Where one kind of record lives: a folder of JSON files in development, a blob container in production. This is the only thing that differs between the two, so a repository is written once
/// against this and carries the business rules (soft delete, ordering, defaults) in one place instead of twice. On top of keeping documents by name it can list them, for repositories that
/// answer "all of them" or "all of one owner's".
/// </summary>
public interface IDocumentStore : IKeyedDocumentStore
{
    /// <summary>Every readable document, deserialized; a corrupt or unreadable one is skipped.</summary>
    IAsyncEnumerable<(string Name, T Value)> ListAsync<T>(CancellationToken cancellationToken = default);

    /// <summary>Every document's name with a token that changes whenever its content does, without reading the content.</summary>
    IAsyncEnumerable<(string Name, string Version)> ListNamesWithVersionAsync(CancellationToken cancellationToken = default);
}
