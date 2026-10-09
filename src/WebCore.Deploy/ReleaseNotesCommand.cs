/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Globalization;
using System.Text;
using System.Text.Json;
using WebCore.Operations;

namespace WebCore.Deploy;

/// <summary>
/// Release notes from git: each production deploy is a tag (release-yyyy-MM-dd-HHmm, UTC), and a release is the commits between two tags.
/// The notes are written to release-notes.json, which the build ships and /admin/releases reads.
/// </summary>
internal static class ReleaseNotesCommand
{
    // The hash of git's empty tree: diffing against it gives the size of the whole first release.
    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static string GenerateJson(string repoRoot) => JsonSerializer.Serialize(Generate(repoRoot), Json);

    public static ReleaseNotes Generate(string root)
    {
        string Git(string args) => DeployTool.Run("git", args, root).Output;

        var remote = Git("remote get-url origin").Trim();
        var repoUrl = remote.EndsWith(".git", StringComparison.Ordinal) ? remote[..^4] : remote;
        var head = Git("rev-parse HEAD").Trim();

        var tags = Git("for-each-ref refs/tags --format=%(refname:short)")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Select(t => (Name: t, At: ReleaseNotesParser.ParseReleaseTag(t)))
            .Where(t => t.At is not null)
            .OrderBy(t => t.At)
            .ToList();

        var releases = new List<Release>();
        string? previous = null;
        foreach (var (name, at) in tags)
        {
            var range = previous is null ? name : $"{previous}..{name}";
            var commits = ReleaseNotesParser.ParseLog(Git($"log {range} --format={ReleaseNotesParser.LogFormat}"));
            var (files, added, removed) = ReleaseNotesParser.ParseShortStat(Git($"diff --shortstat {previous ?? EmptyTree} {name}"));
            releases.Add(new Release(name, at, Git($"rev-list -n1 {name}").Trim(), previous, commits, files, added, removed));
            previous = name;
        }

        // Whatever is in the build beyond the last tag has not been through a production deploy yet.
        var tail = ReleaseNotesParser.ParseLog(Git(previous is null ? $"log --format={ReleaseNotesParser.LogFormat}" : $"log {previous}..HEAD --format={ReleaseNotesParser.LogFormat}"));
        if (tail.Count > 0)
        {
            var (files, added, removed) = ReleaseNotesParser.ParseShortStat(Git($"diff --shortstat {previous ?? EmptyTree} HEAD"));
            releases.Add(new Release(null, null, head, previous, tail, files, added, removed));
        }

        releases.Reverse();
        return new ReleaseNotes(repoUrl, DateTimeOffset.UtcNow, head, releases);
    }

    public static string ToMarkdown(ReleaseNotes notes)
    {
        var sb = new StringBuilder("# Release notes\n\nGenerated from git by `tools/Deploy notes`. Newest first.\n");
        foreach (var release in notes.Releases)
        {
            sb.Append("\n## ").Append(release.Name is null ? "In the current build (not yet deployed to production)" : $"{release.Name} ({release.DeployedAt:yyyy-MM-dd HH:mm} UTC)").Append('\n');
            if (notes.CompareUrl(release) is { } compare)
            {
                sb.Append($"\n[Compare on GitHub]({compare}) - {release.FilesChanged} files, +{release.Insertions}/-{release.Deletions}\n");
            }

            foreach (var category in Enum.GetValues<ChangeCategory>())
            {
                var inCategory = release.Commits.Where(c => c.Category == category).ToList();
                if (inCategory.Count == 0)
                {
                    continue;
                }

                sb.Append($"\n### {CommitClassifier.Heading(category)}\n\n");
                foreach (var commit in inCategory)
                {
                    sb.Append($"- {commit.Subject} ([{commit.ShortSha}]({notes.CommitUrl(commit.Sha)}), {commit.Date:yyyy-MM-dd})\n");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>Tags the commit that was just deployed to production. Skips a commit that already has a release tag (a redeploy).</summary>
    public static void TagDeployedBuild(string root, string versionSha, DateTimeOffset deployedAt)
    {
        var full = DeployTool.Run("git", $"rev-parse {versionSha}", root).Output.Trim();
        if (full.Length < 40)
        {
            Console.WriteLine($"Could not resolve {versionSha} to a commit, so no release tag was made.");
            return;
        }

        var existing = DeployTool.Run("git", $"tag --points-at {full}", root).Output.Split('\n').Select(t => t.Trim()).FirstOrDefault(t => ReleaseNotesParser.ParseReleaseTag(t) is not null);
        if (existing is not null)
        {
            Console.WriteLine($"Commit {versionSha} is already release {existing}.");
            return;
        }

        var tag = ReleaseNotesParser.ReleaseTagFor(deployedAt);
        var (code, output) = DeployTool.Run("git", $"tag {tag} {full}", root);
        if (code != 0)
        {
            Console.WriteLine("Could not create the release tag: " + output.Trim());
            return;
        }

        var (pushCode, pushOutput) = DeployTool.Run("git", $"push origin {tag}", root);
        Console.WriteLine(pushCode == 0 ? $"Tagged {tag} and pushed it." : $"Tagged {tag} locally, but the push failed: {pushOutput.Trim()}");
    }

    /// <summary>One-off: turns Azure's own record of past production deploys into release tags on the commit that was newest at the time.</summary>
    public static int BackfillTags(string root, IEnumerable<DateTimeOffset> deployTimes, bool apply)
    {
        var made = 0;
        var seen = new HashSet<string>();
        foreach (var at in deployTimes.OrderBy(t => t))
        {
            var iso = at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var commit = DeployTool.Run("git", $"rev-list -1 --before={iso} HEAD", root).Output.Trim();
            var tag = ReleaseNotesParser.ReleaseTagFor(at);
            if (commit.Length < 40 || !seen.Add(commit))
            {
                Console.WriteLine($"  {tag}: same commit as an earlier deploy - skipped");
                continue;
            }

            if (DeployTool.Run("git", $"rev-parse -q --verify refs/tags/{tag}", root).Code == 0)
            {
                Console.WriteLine($"  {tag}: already exists");
                continue;
            }

            var subject = DeployTool.Run("git", $"log -1 --format=%s {commit}", root).Output.Trim();
            Console.WriteLine($"  {tag} -> {commit[..8]} \"{subject}\"");
            if (apply)
            {
                DeployTool.Run("git", $"tag {tag} {commit}", root);
                made++;
            }
        }

        if (apply && made > 0)
        {
            var (code, output) = DeployTool.Run("git", "push origin --tags", root);
            Console.WriteLine(code == 0 ? $"Created and pushed {made} tag(s)." : "Push failed: " + output.Trim());
        }
        else if (!apply)
        {
            Console.WriteLine("Dry run - add --apply to create these tags.");
        }

        return 0;
    }
}
