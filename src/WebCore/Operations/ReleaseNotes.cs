/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Globalization;
using System.Text.RegularExpressions;

namespace WebCore.Operations;

public enum ChangeCategory
{
    Feature,
    Fix,
    Improvement,
    BehindTheScenes,
}

/// <summary>One commit as shown in the release notes.</summary>
public record ReleaseCommit(string Sha, DateTimeOffset Date, string Subject, string Body, ChangeCategory Category)
{
    public string ShortSha => Sha.Length > 8 ? Sha[..8] : Sha;
}

/// <summary>The commits that went out together (one production deploy), or the not-yet-released tail of the build.</summary>
/// <param name="Name">The release tag, e.g. release-2026-09-25-1609; null for the unreleased tail.</param>
/// <param name="DeployedAt">When it went to production (UTC); null for the unreleased tail.</param>
/// <param name="Sha">The commit the release points at.</param>
/// <param name="PreviousName">The tag it is compared against on GitHub; null for the very first release.</param>
public record Release(string? Name, DateTimeOffset? DeployedAt, string Sha, string? PreviousName, IReadOnlyList<ReleaseCommit> Commits, int FilesChanged, int Insertions, int Deletions)
{
    public int Count(ChangeCategory category) => Commits.Count(c => c.Category == category);
}

/// <summary>Everything the release-notes page shows. Generated at build time by tools/Deploy from git, shipped as release-notes.json.</summary>
public record ReleaseNotes(string RepoUrl, DateTimeOffset GeneratedAt, string BuiltFromSha, IReadOnlyList<Release> Releases)
{
    public string CommitUrl(string sha) => $"{RepoUrl}/commit/{sha}";

    public string? CompareUrl(Release release) =>
        release.PreviousName is null || release.Name is null ? null : $"{RepoUrl}/compare/{release.PreviousName}...{release.Name}";
}

/// <summary>Sorts a commit message into a heading. Commit subjects are free text, so this is a best-effort reading of the wording.</summary>
public static class CommitClassifier
{
    private static readonly Regex Fix = new(@"\b(fix(es|ed)?|bug|broken|crash(es)?|repair(s|ed)?|resolve[sd]?|correct(s|ed)?|regression|hotfix)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Behind = new(@"\b(docs?|documentation|readme|claude\.md|unit tests?|tests for|test coverage|refactor(ed|s)?|chore|tooling|runbook|deploy(ment)? tool|ci|gitignore|cleanup|clean up|rename[sd]?|lint|build script|bump(s|ed)?|dependabot|dependenc(y|ies))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Improve = new(@"\b(improv\w+|polish\w*|tweak\w*|tun(e|es|ed|ing)|updat\w+|refin\w+|rework\w*|simplif\w+|faster|speed\w*|reduc\w+|hide[sd]?|remov\w+|drop(s|ped)?|shorten\w*|tighten\w*|smaller|larger|change[sd]?|switch\w*|move[sd]?|keeps?|stop(s)?|no longer|instead)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ChangeCategory Classify(string subject)
    {
        // Fixes win over everything: "Fix the docs link" is still a fix. Work on the tooling/docs/tests is next; anything that reads as
        // adding something is a feature; the rest are improvements to what already existed.
        if (Fix.IsMatch(subject))
        {
            return ChangeCategory.Fix;
        }

        if (Behind.IsMatch(subject))
        {
            return ChangeCategory.BehindTheScenes;
        }

        // Commit subjects here are written feature-first ("Rounds and timed breaks: ..."), so anything that is not clearly a tweak to
        // something that already existed counts as a new feature.
        return Improve.IsMatch(subject) ? ChangeCategory.Improvement : ChangeCategory.Feature;
    }

    public static string Heading(ChangeCategory category) => category switch
    {
        ChangeCategory.Feature => "New features",
        ChangeCategory.Fix => "Bug fixes",
        ChangeCategory.Improvement => "Improvements",
        _ => "Behind the scenes",
    };
}

/// <summary>Turns the output of git into <see cref="ReleaseNotes"/>. Pure text parsing so it can be tested without a repository.</summary>
public static class ReleaseNotesParser
{
    private const char FieldSeparator = '\u001f';
    private const char RecordSeparator = '\u001e';

    /// <summary>The git log format this parser reads: <c>git log --format="%H%x1f%cI%x1f%s%x1f%b%x1e"</c>.</summary>
    public const string LogFormat = "%H%x1f%cI%x1f%s%x1f%b%x1e";

    public static IReadOnlyList<ReleaseCommit> ParseLog(string gitLogOutput)
    {
        var commits = new List<ReleaseCommit>();
        foreach (var record in gitLogOutput.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Trim('\r', '\n').Split(FieldSeparator);
            if (fields.Length < 3 || fields[0].Length < 7 || !DateTimeOffset.TryParse(fields[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                continue;
            }

            var body = fields.Length > 3 ? CleanBody(fields[3]) : string.Empty;
            commits.Add(new ReleaseCommit(fields[0].Trim(), date.ToUniversalTime(), fields[2].Trim(), body, CommitClassifier.Classify(fields[2])));
        }

        return commits;
    }

    // Drops the "Co-Authored-By" trailer lines; keeps the explanatory paragraph.
    private static string CleanBody(string body) =>
        string.Join("\n", body.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !l.StartsWith("Co-Authored-By:", StringComparison.OrdinalIgnoreCase))).Trim();

    /// <summary>"release-2026-09-25-1609" -> 2026-09-25 16:09 UTC, or null when the tag is not a release tag.</summary>
    public static DateTimeOffset? ParseReleaseTag(string tag)
    {
        // The shape is checked by the pattern; whether the moment exists (no 30 February, no 25:00) is checked by the
        // date parser, so a tag that only looks like a release tag is skipped rather than failing the whole log.
        var match = Regex.Match(tag, @"^release-(\d{4}-\d{2}-\d{2}-\d{4})$");
        if (!match.Success || !DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
        {
            return null;
        }

        return new DateTimeOffset(when, TimeSpan.Zero);
    }

    public static string ReleaseTagFor(DateTimeOffset deployedAt) => "release-" + deployedAt.ToUniversalTime().ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);

    /// <summary>Parses "12 files changed, 340 insertions(+), 15 deletions(-)" (git diff --shortstat); zeros when empty.</summary>
    public static (int Files, int Insertions, int Deletions) ParseShortStat(string shortStat)
    {
        // A count too large for int reads as zero, as a missing one does; one odd number must not fail the whole release page.
        int Number(string pattern) =>
            Regex.Match(shortStat, pattern) is { Success: true } m && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;
        return (Number(@"(\d+) files? changed"), Number(@"(\d+) insertions?"), Number(@"(\d+) deletions?"));
    }
}
