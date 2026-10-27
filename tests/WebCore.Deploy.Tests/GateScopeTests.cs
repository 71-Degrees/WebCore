/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Deploy;

namespace WebCore.Deploy.Tests;

public class GateScopeTests
{
    [Fact]
    public void UnknownChange_RunsEveryStage()
    {
        Assert.True(GateScope.NeedsScripts(null));
        Assert.True(GateScope.NeedsCompile(null));
    }

    [Fact]
    public void DocumentationOnly_RunsNeitherStage()
    {
        string[] changed = ["docs/log/2026-10-27.md", "CLAUDE.md", "docs/machines.json", "quality/history.json"];

        Assert.False(GateScope.NeedsScripts(changed));
        Assert.False(GateScope.NeedsCompile(changed));
    }

    [Theory]
    [InlineData("src/App/Program.cs")]
    [InlineData("src/App/Pages/Home.razor")]
    [InlineData("src/App/App.csproj")]
    [InlineData("Directory.Build.props")]
    [InlineData("src/App/packages.lock.json")]
    [InlineData("global.json")]
    [InlineData("tools/WebCore")]
    public void CompilerInput_RunsTheCompileButNotTheScriptCheck(string path)
    {
        Assert.True(GateScope.NeedsCompile([path]));
        Assert.Equal(path == "tools/WebCore", GateScope.NeedsScripts([path]));
    }

    [Theory]
    [InlineData("src/App/wwwroot/js/host.js")]
    [InlineData("src/App/wwwroot/js/types.d.ts")]
    [InlineData("tsconfig.json")]
    [InlineData("package-lock.json")]
    [InlineData("eslint.config.mjs")]
    [InlineData("tools/WebCore")]
    public void ScriptInput_RunsTheScriptCheckButNotTheCompile(string path)
    {
        Assert.True(GateScope.NeedsScripts([path]));
        Assert.Equal(path == "tools/WebCore", GateScope.NeedsCompile([path]));
    }

    [Fact]
    public void AToolsSourceFileIsNotMistakenForASubmodulePointer() =>
        Assert.False(GateScope.NeedsScripts(["tools/Ship/NightlyCommand.cs"]));

    [Fact]
    public void ChangeSignature_IgnoresLineNumbersAndContext()
    {
        const string before = "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -10,3 +10,3 @@ class A\n context\n-old\n+new\n context\n";
        const string rebased = "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -42,3 +42,3 @@ class A\n other context\n-old\n+new\n more\n";
        const string different = "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -10,3 +10,3 @@ class A\n-old\n+newer\n";

        Assert.Equal(GateScope.ChangeSignature(before), GateScope.ChangeSignature(rebased));
        Assert.NotEqual(GateScope.ChangeSignature(before), GateScope.ChangeSignature(different));
    }

    [Fact]
    public void UpstreamCommits_TouchingOtherFilesLeaveTheGateValid() =>
        Assert.True(GateScope.UpstreamLeavesGateValid(["src/A.cs", "tests/ATests.cs"], ["docs/log/x.md", "src/B.cs", "docs/inbox/y.md"]));

    [Theory]
    [InlineData("src/A.cs")]
    [InlineData("src/App/App.csproj")]
    [InlineData("Directory.Build.props")]
    [InlineData("packages.lock.json")]
    [InlineData("tools/WebCore")]
    public void UpstreamCommits_TouchingAChangedFileOrAProjectFileInvalidateTheGate(string arrived) =>
        Assert.False(GateScope.UpstreamLeavesGateValid(["src/A.cs"], [arrived]));
}
