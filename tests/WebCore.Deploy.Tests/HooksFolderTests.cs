/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Diagnostics;

namespace WebCore.Deploy.Tests;

public sealed class HooksFolderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "webcore-hooks-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // git marks some of its files read-only
        foreach (var file in Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_folder, recursive: true);
    }

    private static void Git(string workingDirectory, string arguments)
    {
        var info = new ProcessStartInfo("git", arguments) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(info)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {arguments}: {process.StandardError.ReadToEnd()}");
    }

    [Fact]
    public void InAWorktree_TheHooksAreTheMainRepositorysFolder_NotADotGitFolderThatIsNotThere()
    {
        var main = Path.Combine(_folder, "main");
        var worktree = Path.Combine(_folder, "worktree");
        Directory.CreateDirectory(main);
        Git(main, "init -q");
        Git(main, "-c user.name=t -c user.email=t@example.test commit -q --allow-empty -m first");
        Git(main, $"worktree add -q --detach \"{worktree}\"");

        var fromMain = DeployTool.HooksFolder(main);
        var fromWorktree = DeployTool.HooksFolder(worktree);

        Assert.Equal(Path.GetFullPath(Path.Combine(main, ".git", "hooks")), fromMain);
        Assert.Equal(fromMain, fromWorktree);
    }
}
