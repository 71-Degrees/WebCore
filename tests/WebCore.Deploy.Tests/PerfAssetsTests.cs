/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Deploy.Tests;

public class PerfAssetsTests
{
    [Fact]
    public void StampedAndFingerprintedFilesAreTimed_PlainAndOutsideOnesAreNot()
    {
        const string html = """
            <link rel="stylesheet" href="/app.css?v=3f8a1c2e">
            <link rel="stylesheet" href="app.vwrdrcqh8w.css">
            <script src="js/theme.mfpffbovhd.js"></script>
            <script src="js/plain.js"></script>
            <script src="https://cdn.example/lib.abcdefghij.js"></script>
            """;

        Assert.Equal(new[] { "/app.css?v=3f8a1c2e", "app.vwrdrcqh8w.css", "js/theme.mfpffbovhd.js" }, PerfCommands.VersionedAssets(html));
    }

    [Fact]
    public void AFontKeptAWeekPasses_AShortOneFails_AndASiteWithNoFontIsNotFaulted()
    {
        Assert.Null(PerfCommands.FontProblem(System.Net.HttpStatusCode.OK, TimeSpan.FromDays(30)));
        Assert.NotNull(PerfCommands.FontProblem(System.Net.HttpStatusCode.OK, TimeSpan.FromHours(1)));
        Assert.NotNull(PerfCommands.FontProblem(System.Net.HttpStatusCode.OK, null));
        Assert.Null(PerfCommands.FontProblem(System.Net.HttpStatusCode.NotFound, null));
    }
}
