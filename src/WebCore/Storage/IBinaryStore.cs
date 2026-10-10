/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Storage;

/// <summary>
/// Where raw files (logos, crops, audio clips) live: a folder in development, a blob container in production. The binary counterpart of <see cref="IDocumentStore"/>; a name is the file or blob name,
/// extension included.
/// </summary>
public interface IBinaryStore
{
    /// <summary>The file's bytes, or null when there is none.</summary>
    Task<byte[]?> GetBytesAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Writes the file, replacing any existing one; a reader never sees a half-written file.</summary>
    Task SetBytesAsync(string name, byte[] bytes, CancellationToken cancellationToken = default);

    /// <summary>Removes the file; false when there was none.</summary>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Every file's name and last-modified time, without reading any content.</summary>
    IAsyncEnumerable<(string Name, DateTimeOffset LastModified)> ListNamesWithModifiedAsync(CancellationToken cancellationToken = default);
}
