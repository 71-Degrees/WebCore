/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Storage;

/// <summary>What the file-backed stores share: deleting a stored file, answering whether there was one.</summary>
internal static class FileOps
{
    public static Task<bool> DeleteAsync(string path)
    {
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }
}
