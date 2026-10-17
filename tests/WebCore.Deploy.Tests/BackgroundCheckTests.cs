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
    public void TheRunIsRestartedTheWayTheToolWasStarted()
    {
        // dotnet <dll>: the dll is the host's first argument. dotnet run: the host is the tool's own executable and takes the command directly.
        Assert.Equal("\"C:\\t\\Deploy.dll\" background-check --commit abc", BackgroundCheck.LaunchArguments(@"C:\Program Files\dotnet\dotnet.exe", @"C:\t\Deploy.dll", "abc"));
        Assert.Equal("background-check --commit abc", BackgroundCheck.LaunchArguments(@"C:\t\Deploy.exe", @"C:\t\Deploy.dll", "abc"));
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

    [Fact]
    public void ANewPush_StopsTheRunOfTheOlderOneBeforeItsCopyOfTheToolIsReplaced()
    {
        var root = Path.Combine(Path.GetTempPath(), "webcore-bg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "reports", "background"));
        using var older = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ping", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            File.WriteAllText(Path.Combine(root, "reports", "background", "running.pid"), older.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

            BackgroundCheck.StopPreviousRun(root);

            Assert.True(older.WaitForExit(5000));
            BackgroundCheck.StopPreviousRun(root);   // nothing left to stop is not an error
        }
        finally
        {
            if (!older.HasExited)
            {
                older.Kill();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheRunWorksFromACopyOfTheTool_SoThePushThatRebuildsTheToolIsNeverBlocked()
    {
        var root = Path.Combine(Path.GetTempPath(), "webcore-bg-" + Guid.NewGuid().ToString("N"));
        var tool = Path.Combine(root, "tools", "Deploy", "bin");
        Directory.CreateDirectory(Path.Combine(tool, "runtimes"));
        File.WriteAllText(Path.Combine(tool, "Deploy.dll"), "dll");
        File.WriteAllText(Path.Combine(tool, "Deploy.exe"), "exe");
        File.WriteAllText(Path.Combine(tool, "runtimes", "native.dll"), "native");
        try
        {
            var (host, assembly) = BackgroundCheck.CopyOfTool(root, Path.Combine(tool, "Deploy.exe"), Path.Combine(tool, "Deploy.dll"), "0123456789abcdef");

            var copy = Path.Combine(root, "reports", "background", "tool", "01234567");
            Assert.Equal(Path.Combine(copy, "Deploy.exe"), host);
            Assert.Equal(Path.Combine(copy, "Deploy.dll"), assembly);
            Assert.True(File.Exists(Path.Combine(copy, "runtimes", "native.dll")));

            // a later commit takes the place of the earlier copy
            BackgroundCheck.CopyOfTool(root, Path.Combine(tool, "Deploy.exe"), Path.Combine(tool, "Deploy.dll"), "fedcba9876543210");
            Assert.False(Directory.Exists(copy));

            // through dotnet itself, the host stays dotnet and only the dll moves
            var (dotnetHost, dotnetAssembly) = BackgroundCheck.CopyOfTool(root, @"C:\Program Files\dotnet\dotnet.exe", Path.Combine(tool, "Deploy.dll"), "fedcba9876543210");
            Assert.Equal(@"C:\Program Files\dotnet\dotnet.exe", dotnetHost);
            Assert.EndsWith(Path.Combine("fedcba98", "Deploy.dll"), dotnetAssembly);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
