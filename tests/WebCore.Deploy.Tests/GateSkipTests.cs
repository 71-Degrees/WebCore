/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Deploy;

namespace WebCore.Deploy.Tests;

public class GateSkipTests
{
    private static readonly string[] Baseline =
    {
        "100644 blob aaaa\tsrc/App/Program.cs",
        "100644 blob bbbb\ttests/App.Tests/ProgramTests.cs",
        "160000 commit cccc\ttools/WebCore",
        "100644 blob dddd\tCLAUDE.md",
        "100644 blob eeee\tdocs/dev-and-ops.md",
    };

    [Theory]
    [InlineData("CLAUDE.md", true)]
    [InlineData("README.md", true)]
    [InlineData("docs/performance.md", true)]
    [InlineData("docs/machines.json", true)]
    [InlineData("src/App/Program.cs", false)]
    [InlineData("deploy/deploy.json", false)]
    [InlineData("src/App/wwwroot/docs/page.js", false)]
    public void IsDocumentation_CoversMarkdownAndTheDocsFolderOnly(string path, bool expected) =>
        Assert.Equal(expected, GateSkip.IsDocumentation(path));

    [Fact]
    public void Fingerprint_IgnoresDocumentationChanges()
    {
        var edited = Baseline.Select(l => l.Contains("CLAUDE.md") || l.Contains("docs/") ? l.Replace("dddd", "1111").Replace("eeee", "2222") : l).Append("100644 blob ffff\tdocs/new.md").ToArray();

        Assert.Equal(GateSkip.Fingerprint(Baseline), GateSkip.Fingerprint(edited));
    }

    [Fact]
    public void Fingerprint_ChangesWhenCodeChanges()
    {
        var edited = Baseline.Select(l => l.Replace("aaaa", "9999")).ToArray();

        Assert.NotEqual(GateSkip.Fingerprint(Baseline), GateSkip.Fingerprint(edited));
    }

    [Fact]
    public void Fingerprint_ChangesWhenASubmodulePointerMoves()
    {
        var moved = Baseline.Select(l => l.Replace("cccc", "7777")).ToArray();

        Assert.NotEqual(GateSkip.Fingerprint(Baseline), GateSkip.Fingerprint(moved));
    }

    [Fact]
    public void Fingerprint_DoesNotDependOnListingOrder()
    {
        Assert.Equal(GateSkip.Fingerprint(Baseline), GateSkip.Fingerprint(Baseline.Reverse()));
    }

    [Fact]
    public void Fingerprint_ChangesWhenACodeFileIsAddedOrRemoved()
    {
        Assert.NotEqual(GateSkip.Fingerprint(Baseline), GateSkip.Fingerprint(Baseline.Append("100644 blob 0000\tsrc/App/New.cs")));
        Assert.NotEqual(GateSkip.Fingerprint(Baseline), GateSkip.Fingerprint(Baseline.Where(l => !l.Contains("ProgramTests"))));
    }
}
