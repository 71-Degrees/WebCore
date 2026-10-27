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

    /// <summary>True for the paths that cannot change what the gate finds: documentation, and the quality timeline, which the smell stage writes but never reads.</summary>
    internal static bool IsDocumentation(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.StartsWith("docs/", StringComparison.Ordinal) || path == "quality/history.json";

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
        return last is not null && last.Fingerprint == head ? $"this code already passed the gate on this machine at {last.At} (commit {last.Commit})" : ReasonAfterRebase(root, last);
    }

    /// <summary>The paths this push changes against the branch it goes to, or null when that cannot be known (no upstream, or edits in the tree that are not in HEAD): the gate then runs every stage.</summary>
    internal static IReadOnlyList<string>? ChangedPaths(string root) =>
        HasUncommittedCodeChange(root) || !Upstream(root, out _) ? null : NamesChanged(root, "@{u}", "HEAD");

    // A push race: the other machine pushed while the gate ran, the push was refused, and a clean rebase put the same change on top of the new tip. The gate that passed is still the answer when the change is
    // identical (GateScope.ChangeSignature) and nothing that arrived underneath it touches the changed files, a project file or a submodule pointer.
    private static string? ReasonAfterRebase(string root, GreenState? last)
    {
        if (last?.Upstream is not { Length: > 0 } before || last.Signature is null || last.Files is null || !Upstream(root, out var now) || now == before)
        {
            return null;
        }

        if (DeployTool.Run("git", $"merge-base --is-ancestor {before} {now}", root).Code != 0 || ChangeSignature(root) != last.Signature)
        {
            return null;
        }

        return GateScope.UpstreamLeavesGateValid(last.Files, NamesChanged(root, before, now))
            ? $"the same change passed the gate at {last.At} (commit {last.Commit}); only unrelated files arrived on the branch since, so a rebase after a push race does not repeat it"
            : null;
    }

    /// <summary>Remembers that the code in HEAD passed the whole gate, so pushing it again does not repeat it.</summary>
    internal static void RecordGreen(string root)
    {
        if (HasUncommittedCodeChange(root))
        {
            return;   // what passed was the working tree, not HEAD
        }

        var commit = DeployTool.Run("git", "rev-parse --short=8 HEAD", root).Output.Trim();
        var known = Upstream(root, out var upstream);
        var state = new GreenState(Fingerprint(Listing(root, "HEAD")), commit, DateTime.Now.ToString("yyyy-MM-dd HH:mm"), known ? upstream : null, known ? ChangeSignature(root) : null, known ? NamesChanged(root, "@{u}", "HEAD") : null);
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

    private static bool Upstream(string root, out string sha)
    {
        var (code, output) = DeployTool.Run("git", "rev-parse --verify -q @{u}", root);
        sha = output.Trim();
        return code == 0 && sha.Length > 0;
    }

    private static string[] NamesChanged(string root, string from, string to) =>
        DeployTool.Run("git", $"diff --name-only {from} {to}", root).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ChangeSignature(string root) => GateScope.ChangeSignature(DeployTool.Run("git", "diff --no-color @{u} HEAD", root).Output);

    // Upstream, Signature and Files are what a rebase after a push race is recognised by; a note written before they existed lacks them and simply never matches.
    private sealed record GreenState(string Fingerprint, string Commit, string At, string? Upstream = null, string? Signature = null, string[]? Files = null);
}
