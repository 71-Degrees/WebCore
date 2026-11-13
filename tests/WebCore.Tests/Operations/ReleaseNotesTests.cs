/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Operations;

namespace WebCore.Tests.Operations;

public class ReleaseNotesTests
{
    [Theory]
    [InlineData("Fix the streak bonus double-counting", ChangeCategory.Fix)]
    [InlineData("Production still failing to pull branding: fixed the URL check", ChangeCategory.Fix)]
    [InlineData("Add per-question image search to the quiz editor", ChangeCategory.Feature)]
    [InlineData("Rounds and timed breaks: quiz questions-per-round", ChangeCategory.Feature)]
    [InlineData("Live test: Skip to the end button", ChangeCategory.Feature)]
    [InlineData("Host reveal: hide the leaderboard heading and shrink the flag", ChangeCategory.Improvement)]
    [InlineData("Runbook for the Key Vault and staging app setup", ChangeCategory.BehindTheScenes)]
    [InlineData("Split CLAUDE.md into task guides", ChangeCategory.BehindTheScenes)]
    [InlineData("Bump Azure.Storage.Blobs to 12.29.2", ChangeCategory.BehindTheScenes)]
    [InlineData("Shareable self-hosted preview: /demo/{quizId} plays any quiz solo", ChangeCategory.Feature)]
    [InlineData("Support Openverse as a second image source", ChangeCategory.Feature)]
    public void Classify_ReadsTheWording(string subject, ChangeCategory expected) =>
        Assert.Equal(expected, CommitClassifier.Classify(subject));

    [Fact]
    public void ParseLog_ReadsCommitsAndDropsCoAuthorTrailers()
    {
        var log = "aaaaaaaaaaaaaaaa\u001f2026-09-25T12:00:00-04:00\u001fFix a thing\u001fMore detail here.\n\nCo-Authored-By: Someone <x@y.z>\n\u001e\n"
                + "bbbbbbbbbbbbbbbb\u001f2026-09-24T09:30:00+00:00\u001fAdd a widget\u001f\u001e\n";

        var commits = ReleaseNotesParser.ParseLog(log);

        Assert.Equal(2, commits.Count);
        Assert.Equal("aaaaaaaa", commits[0].ShortSha);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 16, 0, 0, TimeSpan.Zero), commits[0].Date);
        Assert.Equal("More detail here.", commits[0].Body);
        Assert.Equal(ChangeCategory.Fix, commits[0].Category);
        Assert.Equal(ChangeCategory.Feature, commits[1].Category);
        Assert.Equal(string.Empty, commits[1].Body);
    }

    [Fact]
    public void ReleaseTags_RoundTrip()
    {
        var when = new DateTimeOffset(2026, 9, 25, 16, 9, 0, TimeSpan.Zero);
        var tag = ReleaseNotesParser.ReleaseTagFor(when);

        Assert.Equal("release-2026-09-25-1609", tag);
        Assert.Equal(when, ReleaseNotesParser.ParseReleaseTag(tag));
        Assert.Null(ReleaseNotesParser.ParseReleaseTag("v1.0"));
    }

    [Fact]
    public void ParseShortStat_ReadsGitsSummaryLine()
    {
        Assert.Equal((12, 340, 15), ReleaseNotesParser.ParseShortStat(" 12 files changed, 340 insertions(+), 15 deletions(-)"));
        Assert.Equal((1, 2, 0), ReleaseNotesParser.ParseShortStat(" 1 file changed, 2 insertions(+)"));
        Assert.Equal((0, 0, 0), ReleaseNotesParser.ParseShortStat(string.Empty));
    }

    [Fact]
    public void CompareUrl_NeedsBothTags()
    {
        var notes = new ReleaseNotes("https://github.com/o/r", DateTimeOffset.UtcNow, "abc", Array.Empty<Release>());
        var first = new Release("release-2026-09-24-1646", null, "a", null, Array.Empty<ReleaseCommit>(), 0, 0, 0);
        var second = first with { Name = "release-2026-09-25-1609", PreviousName = "release-2026-09-24-1646" };

        Assert.Null(notes.CompareUrl(first));
        Assert.Equal("https://github.com/o/r/compare/release-2026-09-24-1646...release-2026-09-25-1609", notes.CompareUrl(second));
        Assert.Equal("https://github.com/o/r/commit/abc", notes.CommitUrl("abc"));
    }
}
