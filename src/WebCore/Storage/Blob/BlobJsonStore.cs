/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace WebCore.Storage.Blob;

/// <summary>
/// Generic blob primitives shared by every Blob*Repository - the blob-storage counterpart to the
/// local Json*/File* repositories' common temp-file-then-move/enumerate/read-with-JsonException-
/// catch pattern. Unlike a local file write, a blob PUT is already atomic on its own (no reader
/// can ever observe a half-written blob), so there's no temp-blob-then-rename dance needed here -
/// that local-disk workaround simply doesn't apply to blob storage.
///
/// Covers both JSON-serialized records (every repository except sponsor logos) and raw bytes
/// (<see cref="GetBytesAsync"/>/<see cref="SetBytesAsync"/>, used by
/// <c>SponsorLogoRepository</c>) against the same container, rather than duplicating the
/// container-management/exists/delete plumbing in a second near-identical class.
/// </summary>
public class BlobJsonStore : IDocumentStore, IBinaryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly BlobContainerClient _container;
    private readonly object _ensureLock = new();
    private Task? _ensure;

    public BlobJsonStore(string connectionString, string containerName)
    {
        // No storage call here: the container is created the first time something has to write to it or list it (reads and deletes on a missing
        // container already answer "not there"). Creating every store's container at construction cost ~100 ms each, blocking a thread, on every restart.
        _container = new BlobContainerClient(connectionString, containerName);
    }

    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        Task pending;
        lock (_ensureLock)
        {
            pending = _ensure ??= _container.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None);
        }

        try
        {
            await pending.ConfigureAwait(false);
        }
        catch
        {
            // A failed attempt must not be remembered: the next write or list tries again.
            lock (_ensureLock)
            {
                if (ReferenceEquals(_ensure, pending))
                {
                    _ensure = null;
                }
            }

            throw;
        }
    }

    public async Task<T?> GetAsync<T>(string name, CancellationToken cancellationToken = default)
    {
        var blob = _container.GetBlobClient(name);
        try
        {
            var response = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            return response.Value.Content.ToObjectFromJson<T>(SerializerOptions);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return default;
        }
        catch (JsonException)
        {
            // Matches every local repo's "corrupt file -> treat as absent, don't throw" behavior.
            return default;
        }
    }

    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        var response = await _container.GetBlobClient(name).ExistsAsync(cancellationToken).ConfigureAwait(false);
        return response.Value;
    }

    /// <summary>Writes a value, overwriting any existing blob under this name.</summary>
    public Task SetAsync<T>(string name, T value, CancellationToken cancellationToken = default) =>
        SetBytesAsync(name, SerializeToBytes(value), cancellationToken);

    /// <summary>
    /// Writes a value only if no blob already exists under this name - returns false (without
    /// writing) if one already does. The blob-storage equivalent of the local user repository's
    /// <c>File.Move(overwrite: false)</c> race guard against two concurrent registrations for the
    /// same email both succeeding.
    /// </summary>
    public async Task<bool> TrySetIfNotExistsAsync<T>(string name, T value, CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken).ConfigureAwait(false);
        var blob = _container.GetBlobClient(name);
        using var stream = new MemoryStream(SerializeToBytes(value));
        try
        {
            await blob.UploadAsync(
                stream,
                new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            return false;
        }
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        var response = await _container.GetBlobClient(name).DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.Value;
    }

    /// <summary>Every record in the container, deserialized - corrupt/unreadable blobs are
    /// silently skipped, matching the local repos' ListAsync behavior.</summary>
    public async IAsyncEnumerable<(string Name, T Value)> ListAsync<T>([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var name in ListNamesAsync(cancellationToken).ConfigureAwait(false))
        {
            var value = await GetAsync<T>(name, cancellationToken).ConfigureAwait(false);
            if (value is not null)
            {
                yield return (name, value);
            }
        }
    }

    /// <summary>Raw blob names, no deserialization - used where a repository needs to act on a
    /// name (e.g. deleting a matching blob) without necessarily materializing every value.</summary>
    public async IAsyncEnumerable<string> ListNamesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var blobItem in _container.GetBlobsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            yield return blobItem.Name;
        }
    }

    /// <summary>Blob names with their last-modified time, no content download - for callers that
    /// need to age-check blobs (e.g. orphan cleanup with a grace window).</summary>
    public async IAsyncEnumerable<(string Name, DateTimeOffset LastModified)> ListNamesWithModifiedAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var blobItem in _container.GetBlobsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            yield return (blobItem.Name, blobItem.Properties.LastModified ?? DateTimeOffset.MinValue);
        }
    }

    /// <summary>Blob names with their ETag (which changes on every write, unlike the one-second last-modified time), no content download - lets a caller
    /// keep its own copy of each record and re-read only the ones whose ETag changed.</summary>
    public async IAsyncEnumerable<(string Name, string Version)> ListNamesWithVersionAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var blobItem in _container.GetBlobsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            yield return (blobItem.Name, blobItem.Properties.ETag?.ToString() ?? blobItem.Properties.LastModified?.ToString("O") ?? string.Empty);
        }
    }

    public async Task<byte[]?> GetBytesAsync(string name, CancellationToken cancellationToken = default)
    {
        var blob = _container.GetBlobClient(name);
        try
        {
            var response = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            return response.Value.Content.ToArray();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task SetBytesAsync(string name, byte[] bytes, CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken).ConfigureAwait(false);
        var blob = _container.GetBlobClient(name);
        using var stream = new MemoryStream(bytes);
        await blob.UploadAsync(stream, overwrite: true, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] SerializeToBytes<T>(T value) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, SerializerOptions));
}
