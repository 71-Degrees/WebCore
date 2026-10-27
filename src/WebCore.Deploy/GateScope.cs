/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Security.Cryptography;
using System.Text;

namespace WebCore.Deploy;

/// <summary>
/// Decides which parts of the pre-push gate a change can affect, from the paths it touches. The script type check and lint only read browser scripts and their tool settings; the compile only reads C#, Razor
/// and the project and package files. A change that touches neither kind of file leaves that stage's answer as it was, so the stage is not run (the tests and the smell check always are). Only the listed kinds of file and any submodule pointer count;
/// everything else (documents, pictures, data, styles) cannot change either answer.
/// </summary>
internal static class GateScope
{
    private static readonly string[] ScriptExtensions = [".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx"];

    private static readonly string[] CompileExtensions = [".cs", ".razor", ".cshtml", ".csproj", ".props", ".targets", ".sln", ".slnx", ".resx"];

    private static readonly string[] ScriptSettingFiles = ["package.json", "package-lock.json", "tsconfig.json", "jsconfig.json", "eslint.config.mjs", "eslint.config.js", ".eslintrc.json"];

    private static readonly string[] CompileSettingFiles = ["global.json", "nuget.config", "packages.lock.json", "directory.build.props", "directory.packages.props"];

    /// <summary>True when a change to these paths could change the answer of the script type check or lint (null means the changed paths are not known).</summary>
    internal static bool NeedsScripts(IReadOnlyCollection<string>? changed) =>
        changed is null || changed.Any(p => IsSubmodule(p) || Matches(p, ScriptExtensions, ScriptSettingFiles));

    /// <summary>True when a change to these paths could change whether the apps compile (null means the changed paths are not known).</summary>
    internal static bool NeedsCompile(IReadOnlyCollection<string>? changed) =>
        changed is null || changed.Any(p => IsSubmodule(p) || Matches(p, CompileExtensions, CompileSettingFiles));

    /// <summary>
    /// After a push race and a clean rebase, the gate that passed for the same change can stand when the commits that arrived underneath it touch none of its files, no project or package file
    /// (those change what every project builds) and not a submodule pointer.
    /// </summary>
    internal static bool UpstreamLeavesGateValid(IReadOnlyCollection<string> mine, IReadOnlyCollection<string> arrived)
    {
        var overlap = new HashSet<string>(mine, StringComparer.OrdinalIgnoreCase);
        return arrived.All(p => !overlap.Contains(p) && !IsSubmodule(p) && !Matches(p, [".csproj", ".props", ".targets", ".sln", ".slnx"], [.. CompileSettingFiles, .. ScriptSettingFiles]));
    }

    /// <summary>A fingerprint of what a diff adds and removes, without line numbers or context: the same change rebased onto other commits keeps it, which is how a re-run after a rebase is recognised.</summary>
    internal static string ChangeSignature(string diff)
    {
        var changes = diff.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => (l.StartsWith('+') && !l.StartsWith("+++", StringComparison.Ordinal)) || (l.StartsWith('-') && !l.StartsWith("---", StringComparison.Ordinal)) || l.StartsWith("diff --git", StringComparison.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', changes))));
    }

    // A submodule pointer shows in a diff as a path with no extension under tools/ (for example tools/WebCore): what it holds is unknown, so it counts as both kinds of change.
    private static bool IsSubmodule(string path) => path.StartsWith("tools/", StringComparison.Ordinal) && path.Count(c => c == '/') == 1 && !Path.GetFileName(path).Contains('.', StringComparison.Ordinal);

    private static bool Matches(string path, string[] extensions, string[] files)
    {
        var name = Path.GetFileName(path);
        return extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) || files.Any(f => name.Equals(f, StringComparison.OrdinalIgnoreCase));
    }
}
