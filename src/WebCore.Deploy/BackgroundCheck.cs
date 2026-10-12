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

        Launch(root, $"\"{entry}\" background-check --commit {commit}");
        Console.WriteLine($"Background checks started for {commit[..Math.Min(8, commit.Length)]} (reports/background/status.json says how they go; the report opens when they finish).");
    }

    /// <summary>
    /// Starts the run in a new, hidden process that belongs to nobody: PowerShell's Start-Process gives it its own console, so it holds none of the hook's output handles (git would wait for them to close, which is the
    /// run's last line) and does not end with the hook. Git's own variables (GIT_DIR and the rest, set while a hook runs) are removed first: inherited, they would point every git command the run makes at this repository
    /// instead of the copy it works in.
    /// </summary>
    private static void Launch(string root, string dotnetArguments)
    {
        var quote = (string text) => text.Replace("'", "''", StringComparison.Ordinal);
        var command = $"Start-Process -WindowStyle Hidden -WorkingDirectory '{quote(root)}' -FilePath '{quote(Environment.ProcessPath!)}' -ArgumentList '{quote(dotnetArguments)}'";
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
        var pidFile = Path.Combine(StateFolder(root), "running.pid");
        if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) && pid != Environment.ProcessId)
        {
            try
            {
                using var old = Process.GetProcessById(pid);
                old.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // It had already finished: nothing to stop.
            }
        }

        File.WriteAllText(pidFile, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
