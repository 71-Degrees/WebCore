/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using WebCore.Operations;

namespace WebCore.Tests.Adversarial;

// Deploy guard, commit classification and release-note parsing, with nulls, impossible dates, huge numbers and out-of-range options.
public sealed class OperationsAdversarialTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    // ---- DeployGuard ----

    [Fact]
    public void DeployGuard_NoLiveGames_IsAllowed()
    {
        var (allowed, _) = DeployGuard.Evaluate(Status(), null);

        Assert.True(allowed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void DeployGuard_GamesInProgress_RefusedUnlessTheExactCountIsConfirmed(int? confirmed)
    {
        var status = Status(new ActiveGame("AB12", "Quiz", "Playing", 4, false));

        var (allowed, _) = DeployGuard.Evaluate(status, confirmed);

        Assert.False(allowed);
    }

    [Fact]
    public void DeployGuard_ExactConfirmation_IsAllowed()
    {
        var status = Status(new ActiveGame("AB12", "Quiz", "Playing", 4, false));

        Assert.True(DeployGuard.Evaluate(status, 1).Allowed);
    }

    [Fact]
    public void DeployGuard_TestGamesAndLobbiesAreNotInProgress()
    {
        var status = Status(new ActiveGame("T1", "Test", "Playing", 9, true), new ActiveGame("L1", "Lobby", "Lobby", 0, false));

        Assert.Equal(0, status.InProgress);
        Assert.Equal(0, status.WaitingLobbies);
    }

    [Fact]
    public void DeployGuard_NullGameList_ThrowsNullReference()
    {
        var status = new ServerStatus("1", null, Now, null!);

        Assert.Throws<ArgumentNullException>(() => DeployGuard.Evaluate(status, null));
    }

    // ---- CommitClassifier and release-note parsing ----

    [Theory]
    [InlineData("Fix the docs link", ChangeCategory.Fix)]
    [InlineData("Refactor the cache", ChangeCategory.BehindTheScenes)]
    [InlineData("Rounds and timed breaks: new screen", ChangeCategory.Feature)]
    [InlineData("Tweak the spacing", ChangeCategory.Improvement)]
    [InlineData("", ChangeCategory.Feature)]
    [InlineData("   ", ChangeCategory.Feature)]
    public void CommitClassifier_ReadsTheSubjectInPriorityOrder(string subject, ChangeCategory expected)
    {
        Assert.Equal(expected, CommitClassifier.Classify(subject));
    }

    [Fact]
    public void CommitClassifier_NullSubject_ThrowsArgumentNull()
    {
        Assert.ThrowsAny<ArgumentNullException>(() => CommitClassifier.Classify(null!));
    }

    [Theory]
    [InlineData("release-2026-11-21-0930", 2026, 11, 21, 9, 30)]
    [InlineData("release-2024-02-29-2359", 2024, 2, 29, 23, 59)]
    public void ParseReleaseTag_ValidTags_ReadTheirTime(string tag, int y, int mo, int d, int h, int mi)
    {
        var parsed = ReleaseNotesParser.ParseReleaseTag(tag);

        Assert.Equal(new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero), parsed);
    }

    [Theory]
    [InlineData("v1.0")]
    [InlineData("release-2026-11-21")]
    [InlineData("release-2026-11-21-0930-extra")]
    [InlineData("")]
    public void ParseReleaseTag_NonTags_AreNull(string tag)
    {
        Assert.Null(ReleaseNotesParser.ParseReleaseTag(tag));
    }

    [Theory]
    [InlineData("release-2026-02-30-0000")]
    [InlineData("release-2026-13-01-0000")]
    [InlineData("release-2026-11-21-2560")]
    [InlineData("release-2026-11-21-0960")]
    public void ParseReleaseTag_ImpossibleDateOrTime_ThrowsRatherThanReturningNull_Finding(string tag)
    {
        // A git tag that matches the shape but names no real moment throws out of the parser instead of being skipped.
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => ReleaseNotesParser.ParseReleaseTag(tag));
    }

    [Fact]
    public void ParseReleaseTag_Null_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => ReleaseNotesParser.ParseReleaseTag(null!));
    }

    [Fact]
    public void ReleaseTagFor_ConvertsToUtcBeforeNaming()
    {
        var plusTen = new DateTimeOffset(2026, 11, 21, 19, 30, 0, TimeSpan.FromHours(10));

        Assert.Equal("release-2026-11-21-0930", ReleaseNotesParser.ReleaseTagFor(plusTen));
    }

    [Theory]
    [InlineData("", 0, 0, 0)]
    [InlineData("1 file changed, 2 insertions(+)", 1, 2, 0)]
    [InlineData("3 files changed, 1 deletion(-)", 3, 0, 1)]
    [InlineData("1 file changed, 1 insertion(+), 1 deletion(-)", 1, 1, 1)]
    [InlineData("nothing useful", 0, 0, 0)]
    public void ParseShortStat_ReadsCountsAndDefaultsToZero(string stat, int files, int insertions, int deletions)
    {
        Assert.Equal((files, insertions, deletions), ReleaseNotesParser.ParseShortStat(stat));
    }

    [Fact]
    public void ParseShortStat_AHugeCount_ThrowsOverflow_AsRecorded()
    {
        Assert.Throws<OverflowException>(() => ReleaseNotesParser.ParseShortStat("99999999999 files changed"));
    }

    [Fact]
    public void ParseLog_EmptyOutput_IsEmpty()
    {
        Assert.Empty(ReleaseNotesParser.ParseLog(string.Empty));
    }

    [Fact]
    public void ParseLog_BadRecordsAreSkippedAndGoodOnesKept()
    {
        var good = "abcdef1234567890\u001f2026-11-21T09:00:00+00:00\u001fFix the crash\u001fBody line\nCo-Authored-By: Someone\u001e";
        var shortSha = "abc\u001f2026-11-21T09:00:00+00:00\u001fFix\u001f\u001e";
        var badDate = "abcdef1234567890\u001fnot a date\u001fFix\u001f\u001e";
        var noFields = "abcdef1234567890\u001e";

        var commits = ReleaseNotesParser.ParseLog(good + shortSha + badDate + noFields);

        var only = Assert.Single(commits);
        Assert.Equal("Body line", only.Body);
        Assert.Equal(ChangeCategory.Fix, only.Category);
    }

    [Fact]
    public void ParseLog_SubjectContainingTheFieldSeparator_IsTruncatedToTheFieldBeforeIt_AsRecorded()
    {
        var record = "abcdef1234567890\u001f2026-11-21T09:00:00+00:00\u001fpart one\u001fpart two\u001f\u001e";

        var only = Assert.Single(ReleaseNotesParser.ParseLog(record));

        Assert.Equal("part one", only.Subject);
    }

    [Fact]
    public void ParseLog_NullOutput_ThrowsNullReference()
    {
        Assert.Throws<NullReferenceException>(() => ReleaseNotesParser.ParseLog(null!));
    }

    [Fact]
    public void ReleaseNotes_CompareUrl_IsNullWithoutAPreviousOrCurrentName()
    {
        var notes = new ReleaseNotes("https://example.test/r", Now, "abc", []);

        Assert.Null(notes.CompareUrl(new Release(null, null, "abc", "prev", [], 0, 0, 0)));
        Assert.Null(notes.CompareUrl(new Release("now", null, "abc", null, [], 0, 0, 0)));
    }

    [Fact]
    public void ReleaseCommit_ShortSha_HandlesEmptyAndShortValues()
    {
        Assert.Equal(string.Empty, new ReleaseCommit(string.Empty, Now, "s", string.Empty, ChangeCategory.Fix).ShortSha);
        Assert.Equal("abc", new ReleaseCommit("abc", Now, "s", string.Empty, ChangeCategory.Fix).ShortSha);
    }

    private static ServerStatus Status(params ActiveGame[] games) => new("1", "prod", Now, games);
}
