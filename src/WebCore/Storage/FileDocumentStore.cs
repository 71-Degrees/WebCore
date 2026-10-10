/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace WebCore.Storage;

/// <summary>
/// A folder of JSON files as an <see cref="IDocumentStore"/>. Writes go through a temp file and a move, so a save can never leave a half-written file behind, even when two saves race.
/// </summary>
public sealed class FileDocumentStore : IDocumentStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _folder;
    private const int ReadRetries = 3;

    private readonly object _moveLock = new();

    public FileDocumentStore(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(_folder);
    }

    public async Task<T?> GetAsync<T>(string name, CancellationToken cancellationToken = default)
    {
        var path = PathFor(name);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var stream = File.OpenRead(path);
                return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return default;
            }
            catch (JsonException)
            {
                // The same answer a blob gives for a corrupt document: absent. Callers cannot tell it from "never saved", so the list and the editor simply do not show it.
                return default;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < ReadRetries)
            {
                // A save renames a file into place at this moment; Windows can briefly refuse readers.
                await Task.Delay(10 * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(PathFor(name)));

    public async Task SetAsync<T>(string name, T value, CancellationToken cancellationToken = default)
    {
        var (path, tempPath) = await WriteTempAsync(name, value, cancellationToken).ConfigureAwait(false);
        lock (_moveLock)
        {
            File.Move(tempPath, path, overwrite: true);   // on Windows two moves onto one destination at once can throw; the move itself takes microseconds, so one lock is plenty
        }
    }

    public async Task<bool> TrySetIfNotExistsAsync<T>(string name, T value, CancellationToken cancellationToken = default)
    {
        var (path, tempPath) = await WriteTempAsync(name, value, cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_moveLock)
            {
                File.Move(tempPath, path, overwrite: false);   // refuses when the destination exists, which is what makes two racing creators unable to both succeed
            }

            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(tempPath);
            return false;
        }
    }

    /// <summary>Writes the value to a uniquely named temp file next to its destination, so the final move is the only moment the document changes.</summary>
    private async Task<(string Path, string TempPath)> WriteTempAsync<T>(string name, T value, CancellationToken cancellationToken)
    {
        var path = PathFor(name);
        Directory.CreateDirectory(_folder);   // the folder may have been removed since construction (a cleared cache, a restored backup)
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(value, SerializerOptions), cancellationToken).ConfigureAwait(false);
        return (path, tempPath);
    }


    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) => FileOps.DeleteAsync(PathFor(name));

    public async IAsyncEnumerable<(string Name, T Value)> ListAsync<T>([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var file in JsonFiles())
        {
            if (await GetAsync<T>(file.Name, cancellationToken).ConfigureAwait(false) is { } value)
            {
                yield return (file.Name, value);
            }
        }
    }

    public async IAsyncEnumerable<(string Name, string Version)> ListNamesWithVersionAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var file in JsonFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return (file.Name, $"{file.LastWriteTimeUtc.Ticks}-{file.Length}");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private IEnumerable<FileInfo> JsonFiles() =>
        Directory.Exists(_folder) ? new DirectoryInfo(_folder).EnumerateFiles("*.json") : [];

    private string PathFor(string name) => Path.Combine(_folder, Path.GetFileName(name));
}
