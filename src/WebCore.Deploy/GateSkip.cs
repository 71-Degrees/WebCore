/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WebCore.Deploy;

/// <summary>
/// Knows when the pre-push gate has nothing new to check. The gate (build, tests, scripts, smells) is a function of the checked-in files, so a fingerprint of them says whether it would find anything different:
/// the push is skipped when the fingerprint of HEAD equals the one of the branch it is going to (what is already there was gated when it was pushed) or the one of the last green run on this machine (a retry
/// after a push that failed for another reason). Documentation (`*.md`, `docs/`) is left out of the fingerprint, so a change to it alone never starts a build. Content, not timestamps: a checkout or a touch
/// changes a time without changing the code, and a rebuild with identical inputs finds nothing new.
/// </summary>
internal static class GateSkip
{
    private const string StateFile = "reports/gate-green.json";

    /// <summary>True for the paths that cannot change what the gate finds.</summary>
    internal static bool IsDocumentation(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.StartsWith("docs/", StringComparison.Ordinal);

    /// <summary>The fingerprint of a `git ls-tree -r` listing (`mode type sha TAB path`): a hash of every entry that is not documentation. A submodule is an entry too, so moving its pointer changes it.</summary>
    internal static string Fingerprint(IEnumerable<string> listing)
    {
        var relevant = listing.Where(line => line.Contains('\t') && !IsDocumentation(line[(line.IndexOf('\t') + 1)..])).Order(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', relevant))));
    }

    /// <summary>Why the gate can be skipped, or null when it has to run.</summary>
    internal static string? Reason(string root)
    {
        if (HasUncommittedCodeChange(root))
        {
            return null;
        }

        var head = Fingerprint(Listing(root, "HEAD"));
        if (DeployTool.Run("git", "rev-parse --verify -q @{u}", root) is { Code: 0 } && Fingerprint(Listing(root, "@{u}")) == head)
        {
            return "nothing but documentation differs from the branch this push goes to (that code was checked when it was pushed)";
        }

        var last = ReadState(root);
        return last is not null && last.Fingerprint == head ? $"this code already passed the gate on this machine at {last.At} (commit {last.Commit})" : null;
    }

    /// <summary>Remembers that the code in HEAD passed the whole gate, so pushing it again does not repeat it.</summary>
    internal static void RecordGreen(string root)
    {
        if (HasUncommittedCodeChange(root))
        {
            return;   // what passed was the working tree, not HEAD
        }

        var commit = DeployTool.Run("git", "rev-parse --short=8 HEAD", root).Output.Trim();
        var state = new GreenState(Fingerprint(Listing(root, "HEAD")), commit, DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        var path = Path.Combine(root, StateFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state));
    }

    // Untracked files are not in a push, so they do not count; a modified or staged file that is not documentation means the gate would be testing something HEAD does not hold.
    private static bool HasUncommittedCodeChange(string root) =>
        DeployTool.Run("git", "status --porcelain --untracked-files=no", root).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Length > 3 && !IsDocumentation(line[3..].Trim().Trim('"')));

    private static string[] Listing(string root, string rev) => DeployTool.Run("git", $"ls-tree -r {rev}", root).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static GreenState? ReadState(string root)
    {
        try
        {
            var path = Path.Combine(root, StateFile);
            return File.Exists(path) ? JsonSerializer.Deserialize<GreenState>(File.ReadAllText(path)) : null;
        }
        catch (JsonException)
        {
            return null;   // a damaged note just means the gate runs
        }
    }

    private sealed record GreenState(string Fingerprint, string Commit, string At);
}
