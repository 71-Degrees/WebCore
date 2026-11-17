/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebCore.Deploy;

/// <summary>
/// The slow, informational checks that run after a push instead of during it (<c>deploy.json</c>'s <c>background</c>): coverage, the browser tests, the full analyzer pass, the report. They run on a separate copy
/// of the code - a git worktree of the pushed commit, kept between runs so its builds are incremental - so they never fight the developer's own builds, the running dev server or an edit in progress. A newer push
/// replaces a run still going. Everything they find lands in the real <c>reports</c> folder; <c>reports/background/status.json</c> says how the last run went and <c>latest.log</c> holds its output.
/// </summary>
internal static partial class BackgroundCheck
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    [GeneratedRegex(@"^\s*path\s*=\s*(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex SubmodulePath();

    private static string StateFolder(string root) => Path.Combine(root, "reports", "background");

    /// <summary>Fills the placeholders a background command may use: {main} (the real working folder), {reports} (its reports folder) and {commit}.</summary>
    internal static string Expand(string text, string main, string commit) =>
        text.Replace("{main}", main, StringComparison.Ordinal).Replace("{reports}", Path.Combine(main, "reports"), StringComparison.Ordinal).Replace("{commit}", commit, StringComparison.Ordinal);

    /// <summary>The submodule folders named in a .gitmodules file.</summary>
    internal static IReadOnlyList<string> SubmodulePaths(string gitmodules) =>
        SubmodulePath().Matches(gitmodules).Select(m => m.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar)).ToList();

    /// <summary>The arguments that start the run again. Started with <c>dotnet &lt;dll&gt;</c> the host is dotnet and the tool's dll is its first argument; started through <c>dotnet run</c> the host is the tool's own executable, which takes the command directly.</summary>
    internal static string LaunchArguments(string? processPath, string entryAssembly, string commit)
    {
        var viaDotnet = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
        return (viaDotnet ? $"\"{entryAssembly}\" " : string.Empty) + $"background-check --commit {commit}";
    }

    /// <summary>
    /// Copies the running tool (its whole output folder) to <c>reports/background/tool/&lt;commit&gt;</c> and says where the host and the tool's dll are in the copy. The host is returned as it is when it is <c>dotnet</c> itself (nothing of
    /// the tool is locked through it). A folder from an earlier commit is removed when it can be; one still in use by a run that has not ended yet is left for the next time.
    /// </summary>
    internal static (string Host, string Assembly) CopyOfTool(string root, string processPath, string entryAssembly, string commit)
    {
        var source = Path.GetDirectoryName(entryAssembly)!;
        var tools = Path.Combine(StateFolder(root), "tool");
        var target = Path.Combine(tools, commit[..Math.Min(8, commit.Length)]);
        foreach (var old in Directory.Exists(tools) ? Directory.GetDirectories(tools).Where(d => !string.Equals(d, target, StringComparison.OrdinalIgnoreCase)) : [])
        {
            try
            {
                Directory.Delete(old, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Windows says "access denied" (not "in use") for a folder whose program is still running.
                Console.WriteLine($"  (left {Path.GetFileName(old)}: a background run still uses it - {ex.Message})");
            }
        }

        if (!Directory.Exists(target))
        {
            CopyFolder(source, target);
        }

        var viaDotnet = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
        return (viaDotnet ? processPath : Path.Combine(target, Path.GetFileName(processPath)), Path.Combine(target, Path.GetFileName(entryAssembly)));
    }

    private static void CopyFolder(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    /// <summary>Starts a background run for the current commit and returns at once: a separate, hidden process that outlives the push (and the git hook that started it).</summary>
    public static void Start(string root)
    {
        if (DeployTool.Config.Background is not { Commands.Length: > 0 })
        {
            return;
        }

        var commit = DeployTool.Run("git", "rev-parse HEAD", root).Output.Trim();
        var entry = Assembly.GetEntryAssembly()?.Location;
        if (commit.Length == 0 || string.IsNullOrEmpty(entry) || Environment.ProcessPath is null)
        {
            Console.WriteLine("  (the background checks could not be started)");
            return;
        }

        // Stop the run of the older push now, not when the new run gets going: the copy of the tool below removes the old copy, which a run still going holds ("left aea3fed8: access denied").
        StopPreviousRun(root);

        // The run goes on for many minutes, so it works from a copy of the tool: started from the tool's own output folder it would hold those files, and the next push (whose hook rebuilds the tool) could not.
        var (host, assembly) = CopyOfTool(root, Environment.ProcessPath, entry, commit);
        Launch(root, host, LaunchArguments(host, assembly, commit));
        Console.WriteLine($"Background checks started for {commit[..Math.Min(8, commit.Length)]} (reports/background/status.json says how they go; the report opens when they finish).");
    }

    /// <summary>
    /// Starts the run in a new, hidden process that belongs to nobody: PowerShell's Start-Process gives it its own console, so it holds none of the hook's output handles (git would wait for them to close, which is the
    /// run's last line) and does not end with the hook. Git's own variables (GIT_DIR and the rest, set while a hook runs) are removed first: inherited, they would point every git command the run makes at this repository
    /// instead of the copy it works in.
    /// </summary>
    private static void Launch(string root, string host, string dotnetArguments)
    {
        var quote = (string text) => text.Replace("'", "''", StringComparison.Ordinal);
        var command = $"Start-Process -WindowStyle Hidden -WorkingDirectory '{quote(root)}' -FilePath '{quote(host)}' -ArgumentList '{quote(dotnetArguments)}'";
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(command);
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            info.Environment.Remove(key);
        }

        using var launcher = Process.Start(info);
        launcher?.WaitForExit(15000);
    }

    /// <summary>background-check [--commit &lt;sha&gt;]: the run itself.</summary>
    public static async Task<int> RunAsync(Options options)
    {
        var settings = DeployTool.Config.Background;
        if (settings is not { Commands.Length: > 0 })
        {
            Console.Error.WriteLine("deploy.json has no background commands.");
            return 2;
        }

        var root = DeployTool.RepoRoot();
        var commit = options.Value("--commit") ?? DeployTool.Run("git", "rev-parse HEAD", root).Output.Trim();
        Directory.CreateDirectory(StateFolder(root));
        ReplacePreviousRun(root);

        var status = new BackgroundStatus { Commit = commit, StartedAt = DateTimeOffset.Now, State = "running" };
        Save(root, status);
        await using var log = new StreamWriter(Path.Combine(StateFolder(root), "latest.log"), append: false) { AutoFlush = true };
        try
        {
            var copy = PrepareCopy(root, commit, settings, log);
            foreach (var command in settings.Commands.Concat(settings.Report is { Length: > 0 } ? [settings.Report] : []))
            {
                var expanded = Expand(command, root, commit);
                var started = Stopwatch.StartNew();
                await log.WriteLineAsync($"\n=== {expanded}");
                var code = await RunInAsync(expanded, copy, settings, root, commit, log);
                status.Steps.Add(new BackgroundStep { Command = expanded, ExitCode = code, Seconds = Math.Round(started.Elapsed.TotalSeconds, 1) });
                Save(root, status);
                if (code != 0)
                {
                    // The push is already lost: the later stages would only cost minutes and bury the failure.
                    await log.WriteLineAsync("\nStopped after the first failing step; the remaining steps were not run.");
                    break;
                }
            }

            status.State = status.Steps.All(s => s.ExitCode == 0) ? "passed" : "failed";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await log.WriteLineAsync("The background run stopped: " + ex.Message);
            status.State = "error";
            status.Error = ex.Message;
        }

        status.FinishedAt = DateTimeOffset.Now;
        Save(root, status);
        File.Delete(Path.Combine(StateFolder(root), "running.pid"));
        return status.State == "passed" ? 0 : 1;
    }

    // Latest wins: a run still going for an older push is stopped, and this one's process id takes its place.
    private static void ReplacePreviousRun(string root)
    {
        StopPreviousRun(root);
        File.WriteAllText(Path.Combine(StateFolder(root), "running.pid"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Stops the run named in running.pid (and everything it started), if it is still going and is not this process. Safe to call twice: the second time there is nothing left.</summary>
    internal static void StopPreviousRun(string root)
    {
        var pidFile = Path.Combine(StateFolder(root), "running.pid");
        if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) && pid != Environment.ProcessId)
        {
            try
            {
                using var old = Process.GetProcessById(pid);
                old.Kill(entireProcessTree: true);
                old.WaitForExit(10000);
            }
            catch (ArgumentException)
            {
                // The usual case: no process has that id any more, so the previous run had finished and there is nothing to stop.
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // It ended while being stopped, or cannot be touched: either way this run goes ahead, and the reason is said.
                Console.Error.WriteLine($"  (the previous background run {pid} could not be stopped: {ex.Message})");
            }
        }
    }

    /// <summary>The copy of the code a run works in: a worktree of the pushed commit (kept, so builds are incremental), the submodules copied from the working folder, and the folders the settings say to link.</summary>
    private static string PrepareCopy(string root, string commit, BackgroundSettings settings, StreamWriter log)
    {
        var copy = Path.Combine(StateFolder(root), "tree");
        DeployTool.Run("git", "worktree prune", root);
        var present = File.Exists(Path.Combine(copy, ".git"));
        var (code, output) = present
            ? DeployTool.Run("git", $"checkout --detach --force {commit}", copy)
            : DeployTool.Run("git", $"worktree add --detach --force \"{copy}\" {commit}", root);
        if (code != 0)
        {
            throw new InvalidOperationException("Could not check out the pushed commit for the background run: " + output.Trim());
        }

        var gitmodules = Path.Combine(root, ".gitmodules");
        foreach (var submodule in File.Exists(gitmodules) ? SubmodulePaths(File.ReadAllText(gitmodules)) : [])
        {
            // The submodules' pinned commits are what the working folder has checked out, so a copy of them is what the pushed commit uses - with no network.
            DeployTool.Run("robocopy", $"\"{Path.Combine(root, submodule)}\" \"{Path.Combine(copy, submodule)}\" /MIR /XD bin obj .git node_modules /NFL /NDL /NJH /NJS /NP /R:1 /W:1", root);
        }

        foreach (var link in settings.Link)
        {
            var target = Path.Combine(root, link);
            var at = Path.Combine(copy, link);
            if (Directory.Exists(target) && !Directory.Exists(at))
            {
                DeployTool.Run("cmd", $"/c mklink /J \"{at}\" \"{target}\"", root);
            }
        }

        log.WriteLine($"Background run for {commit} in {copy}");
        return copy;
    }

    private static async Task<int> RunInAsync(string command, string workingDirectory, BackgroundSettings settings, string main, string commit, StreamWriter log)
    {
        var info = new ProcessStartInfo("cmd", "/c " + command)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var (key, value) in settings.Env)
        {
            info.Environment[key] = Expand(value, main, commit);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start: " + command);
        var gate = new object();
        void Write(string? line)
        {
            if (line is not null)
            {
                lock (gate)
                {
                    log.WriteLine(line);
                }
            }
        }

        process.OutputDataReceived += (_, e) => Write(e.Data);
        process.ErrorDataReceived += (_, e) => Write(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static void Save(string root, BackgroundStatus status) =>
        File.WriteAllText(Path.Combine(StateFolder(root), "status.json"), JsonSerializer.Serialize(status, Json));
}

/// <summary>How the last background run went (<c>reports/background/status.json</c>).</summary>
internal sealed class BackgroundStatus
{
    public string Commit { get; set; } = string.Empty;

    /// <summary>running, passed, failed (a command exited non-zero) or error (the run could not complete).</summary>
    public string State { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public string? Error { get; set; }

    public List<BackgroundStep> Steps { get; set; } = [];
}

internal sealed class BackgroundStep
{
    public string Command { get; set; } = string.Empty;

    public int ExitCode { get; set; }

    public double Seconds { get; set; }
}
