/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Runtime.CompilerServices;

namespace WebCore.Storage;

/// <summary>A folder of raw files as an <see cref="IBinaryStore"/>. Writes go through a temp file and a move, so a reader never sees a half-written file.</summary>
public sealed class FileBinaryStore : IBinaryStore
{
    private const string TempExtension = ".tmp";

    private readonly string _folder;
    private readonly object _moveLock = new();

    public FileBinaryStore(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(_folder);
    }

    public async Task<byte[]?> GetBytesAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = PathFor(name);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : null;
    }

    public async Task SetBytesAsync(string name, byte[] bytes, CancellationToken cancellationToken = default)
    {
        var path = PathFor(name);
        Directory.CreateDirectory(_folder);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + TempExtension;
        await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
        lock (_moveLock)
        {
            File.Move(tempPath, path, overwrite: true);
        }
    }

    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) => FileOps.DeleteAsync(PathFor(name));

    public async IAsyncEnumerable<(string Name, DateTimeOffset LastModified)> ListNamesWithModifiedAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(_folder))
        {
            foreach (var file in new DirectoryInfo(_folder).EnumerateFiles().Where(f => !f.Name.EndsWith(TempExtension, StringComparison.Ordinal)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return (file.Name, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private string PathFor(string name) => Path.Combine(_folder, Path.GetFileName(name));
}
