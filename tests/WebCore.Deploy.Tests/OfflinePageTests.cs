/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Operations;

namespace WebCore.Deploy.Tests;

public class OfflinePageTests
{
    private static readonly Release Latest = new("release-2026-10-16-1200", new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero), "abc", null,
        [new ReleaseCommit("abc", DateTimeOffset.UnixEpoch, "Admin page: counts on every card", string.Empty, ChangeCategory.Feature), new ReleaseCommit("def", DateTimeOffset.UnixEpoch, "Fix the <b>join</b> card", string.Empty, ChangeCategory.Fix)], 3, 10, 2);

    [Fact]
    public void ANotice_BecomesThePage_WithItsWordsItsPictureAndTheReleaseNotesButton()
    {
        var json = """{ "notice": { "heading": "A quick update tonight", "message": "Back soon & better.", "startsAt": "2026-10-16T22:00:00-04:00", "endsAt": "2026-10-16T22:20:00-04:00", "showReleaseNotes": true } }""";

        var page = OfflinePage.Render(OfflinePage.FromNotice(json, "data:image/png;base64,AAAA", new ReleaseNotes("https://example.test", DateTimeOffset.UnixEpoch, "abc", [Latest]))!);

        Assert.Contains("A quick update tonight", page, StringComparison.Ordinal);
        Assert.Contains("Back soon &amp; better.", page, StringComparison.Ordinal);       // the words are encoded
        Assert.Contains("data:image/png;base64,AAAA", page, StringComparison.Ordinal);
        Assert.Contains("See what's new in this update", page, StringComparison.Ordinal);
        Assert.Contains("Admin page: counts on every card", page, StringComparison.Ordinal);
        Assert.Contains("Fix the &lt;b&gt;join&lt;/b&gt; card", page, StringComparison.Ordinal);   // a commit's own markup never becomes markup
        Assert.Contains("fetch('/'", page, StringComparison.Ordinal);                  // reloads only once the site answers
    }

    [Fact]
    public void WithoutANotice_ThereIsNothingToDraw_AndTheNotesButtonCanBeLeftOut()
    {
        Assert.Null(OfflinePage.FromNotice("""{ "notice": null }""", null, null));

        var page = OfflinePage.Render(OfflinePage.FromNotice("""{ "notice": { "message": "m", "showReleaseNotes": false } }""", null, new ReleaseNotes("https://example.test", DateTimeOffset.UnixEpoch, "abc", [Latest]))!);

        Assert.DoesNotContain("See what's new", page, StringComparison.Ordinal);
        Assert.Contains("Better Trivia is being updated", page, StringComparison.Ordinal);   // the default heading
    }
}
