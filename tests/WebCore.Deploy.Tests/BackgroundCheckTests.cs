/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Deploy.Tests;

public sealed class BackgroundCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "webcore-bg-" + Guid.NewGuid().ToString("N"));

    public BackgroundCheckTests() => Directory.CreateDirectory(Path.Combine(_root, "deploy"));

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ThePlaceholdersAreFilledWithTheWorkingFolderItsReportsAndTheCommit()
    {
        var expanded = BackgroundCheck.Expand("tool --out {reports}\\x --repo {main} --sha {commit}", @"C:\site", "abc123");

        Assert.Equal(@"tool --out C:\site\reports\x --repo C:\site --sha abc123", expanded);
    }

    [Fact]
    public void TheSubmodulesAreReadFromGitmodules()
    {
        const string Gitmodules = """
            [submodule "tools/WebCore"]
                path = tools/WebCore
                url = https://github.com/org/WebCore.git
            [submodule "tools/CodeQuality"]
            	path = tools/CodeQuality
            	url = https://github.com/org/CodeQuality.git
            """;

        var paths = BackgroundCheck.SubmodulePaths(Gitmodules);

        Assert.Equal([Path.Combine("tools", "WebCore"), Path.Combine("tools", "CodeQuality")], paths);
    }

    [Fact]
    public void NoGitmodulesMeansNoSubmodules()
    {
        Assert.Empty(BackgroundCheck.SubmodulePaths(string.Empty));
    }

    [Fact]
    public void TheBackgroundSettingsAreReadFromDeployJson_AndAreOffWhenAbsent()
    {
        File.WriteAllText(DeployConfig.PathFor(_root), """{ "app": "Shop", "webProject": "src/Web", "targets": {} }""");
        Assert.Null(DeployConfig.Load(_root).Background);

        File.WriteAllText(
            DeployConfig.PathFor(_root),
            """
            { "app": "Shop", "webProject": "src/Web", "targets": {},
              "background": { "commands": [ "a", "b" ], "report": "r", "env": { "OUT": "{main}" }, "link": [ "node_modules" ] } }
            """);

        var background = DeployConfig.Load(_root).Background!;

        Assert.Equal(["a", "b"], background.Commands);
        Assert.Equal("r", background.Report);
        Assert.Equal("{main}", background.Env["OUT"]);
        Assert.Equal(["node_modules"], background.Link);
    }

    [Fact]
    public void StagesPrintInTheOrderTheyWereRegistered_NoMatterWhichFinishedFirst()
    {
        var timings = new CheckTimings();
        var slow = timings.Begin("slow");
        var fast = timings.Begin("fast");
        slow.WriteLine("slow output");
        fast.WriteLine("fast output");
        fast.Error("fast problem");

        CheckTimings.Run(fast, () => true);
        CheckTimings.Run(slow, () => true);
        var output = new StringWriter();
        var errors = new StringWriter();
        timings.PrintLogs(output, errors);

        Assert.Equal($"slow output{Environment.NewLine}fast output{Environment.NewLine}", output.ToString());
        Assert.Equal($"fast problem{Environment.NewLine}", errors.ToString());
    }

    [Fact]
    public void TheTimingTableListsTheSlowestStageFirst_AndMarksAFailedOne()
    {
        var timings = new CheckTimings();
        var quick = timings.Begin("quick");
        var slow = timings.Begin("slow");
        CheckTimings.Run(quick, () => true);
        CheckTimings.Run(slow, () =>
        {
            Thread.Sleep(60);
            return false;
        });
        var table = new StringWriter();

        timings.Print(table);

        var lines = table.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("slow", lines[1], StringComparison.Ordinal);
        Assert.Contains("(failed)", lines[1], StringComparison.Ordinal);
        Assert.Contains("quick", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void TheTimingsAreAppendedToTheLog_NeverRewritten()
    {
        var timings = new CheckTimings();
        CheckTimings.Run(timings.Begin("one"), () => true);

        timings.Append(_root);
        timings.Append(_root);

        var lines = File.ReadAllLines(Path.Combine(_root, "reports", "check-timings.jsonl"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"stage\":\"one\"", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task StagesRunningAtOnceDoNotMixTheirOutput()
    {
        var timings = new CheckTimings();
        var logs = Enumerable.Range(0, 8).Select(i => timings.Begin("stage " + i)).ToList();

        await Task.WhenAll(logs.Select(log => Task.Run(() => CheckTimings.Run(log, () =>
        {
            for (var n = 0; n < 50; n++)
            {
                log.WriteLine(log.Stage + " line " + n);
            }

            return true;
        }))));
        var output = new StringWriter();
        timings.PrintLogs(output, new StringWriter());

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(8 * 50, lines.Length);
        for (var i = 0; i < 8; i++)
        {
            Assert.All(lines.Skip(i * 50).Take(50), l => Assert.StartsWith("stage " + i + " ", l, StringComparison.Ordinal));
        }
    }
}
