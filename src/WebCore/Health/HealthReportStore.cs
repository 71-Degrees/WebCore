/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Text;
using WebCore.Storage;

namespace WebCore.Health;

/// <summary>
/// Keeps the full HTML reports the tools produce (the quiz checks, the code-quality run) so a signal can link to one that opens on a phone, where a file on the owner's PC cannot be reached. One report per
/// environment and id; publishing again replaces it. The report is shown only to an admin and with a locked-down policy (see <see cref="CspHeader"/>), since it is HTML that a tool wrote.
/// </summary>
public sealed class HealthReportStore(IBinaryStore files)
{
    /// <summary>The largest report kept: a full quiz-checks report is a few hundred kilobytes.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>What a hosted report may do: show itself and nothing else (no scripts, no outside files, no framing by another site).</summary>
    public const string CspHeader = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'; frame-ancestors 'self'";

    public static bool IsValidId(string? id) => DocumentNames.IsValid(id);

    public Task SaveAsync(string environment, string id, string html, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        return bytes.Length > MaxBytes
            ? throw new ArgumentException($"The report is {bytes.Length:N0} bytes; the most kept is {MaxBytes:N0}.", nameof(html))
            : files.SetBytesAsync(Name(environment, id), bytes, cancellationToken);
    }

    public async Task<string?> GetAsync(string environment, string id, CancellationToken cancellationToken = default) =>
        await files.GetBytesAsync(Name(environment, id), cancellationToken).ConfigureAwait(false) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    private static string Name(string environment, string id) =>
        IsValidId(id) ? $"{HealthSignalStore.NormalizeEnvironment(environment)}-{id}.html" : throw new ArgumentException($"'{id}' is not a valid report id.", nameof(id));
}
