/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using WebCore.Operations;

namespace WebCore.Deploy;

internal static partial class DeployTool
{
    // ---- downtime notices ------------------------------------------------------------------------------------------------------------

    private static async Task<int> NoticeAsync(Options options)
    {
        var t = options.Target("notice");
        var at = options.Value("--at") ?? throw new InvalidOperationException("notice needs --at \"yyyy-MM-dd HH:mm\" (your local time).");
        if (!DateTime.TryParse(at, out var local))
        {
            return Fail($"Could not read --at '{at}'.");
        }

        var startsAt = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local));
        DateTimeOffset? endsAt = options.IntValue("--duration") is { } minutes ? startsAt.AddMinutes(minutes) : null;
        var body = new MaintenanceRequest(startsAt, endsAt, options.Value("--message"), options.Flag("--notify"), options.Flag("--dry-run"), options.Value("--test-to"));

        using var response = await Http.SendAsync(Request(HttpMethod.Post, t.Target.Url + Config.Endpoints.Maintenance, ApiKey(t), body));
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            return Fail($"{(int)response.StatusCode}: {text}");
        }

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Console.WriteLine($"Banner {(root.GetProperty("bannerSet").GetBoolean() ? "SET" : "not set")} on {t.Name}: {startsAt.ToLocalTime():f}{(endsAt is null ? "" : " for " + options.IntValue("--duration") + " min")}");
        if (root.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.Object)
        {
            var recipients = email.GetProperty("recipients").EnumerateArray().Select(r => r.GetString()).ToList();
            Console.WriteLine(email.GetProperty("dryRun").GetBoolean()
                ? $"Email (dry run - nothing sent): would go to {recipients.Count}: {string.Join(", ", recipients)}"
                : $"Email: sent {email.GetProperty("sent").GetInt32()}, failed {email.GetProperty("failed").GetInt32()} of {recipients.Count}.");
        }
        else if (!options.Flag("--notify"))
        {
            Console.WriteLine("No email sent (add --notify to email Organizers and Admins).");
        }

        return 0;
    }

    private static async Task<int> NoticeClearAsync(Options options)
    {
        var t = options.Target("notice-clear");
        using var response = await Http.SendAsync(Request(HttpMethod.Delete, t.Target.Url + Config.Endpoints.Maintenance, ApiKey(t)));
        response.EnsureSuccessStatusCode();
        Console.WriteLine($"Downtime banner cleared on {t.Name}.");
        return 0;
    }

    // check: what the pre-push hook and the deploy gate run - all the unit tests, and (for the hook) a compile of the web app into a scratch folder,
    // so it works while a local dev server has the normal output files locked.
    // `check` (what the pre-push hook runs) skips itself when the code it would check is the code already checked (GateSkip); --force runs it anyway. --tests-only is a partial run and is never remembered as green.
    private static bool Check(Options options)
    {
        var root = RepoRoot();
        if (!options.Flag("--force") && GateSkip.Reason(root) is { } reason)
        {
            Console.WriteLine($"Gate skipped: {reason}. (`check --force` runs it anyway.)");
            return true;
        }

        var wholeGate = !options.Flag("--tests-only");
        var passed = RunChecks(root, includeWebBuild: wholeGate, startBackground: options.Flag("--background"));
        if (passed && wholeGate)
        {
            GateSkip.RecordGreen(root);
        }

        return passed;
    }

    private static bool RunChecks(string root, bool includeWebBuild, bool startBackground = false)
    {
        var timings = new CheckTimings();
        var passed = false;
        try
        {
            passed = RunStages(root, includeWebBuild, timings);
            return passed;
        }
        finally
        {
            timings.PrintLogs(Console.Out, Console.Error);
            timings.Print(Console.Out);
            timings.Append(root);
            if (passed && startBackground)
            {
                BackgroundCheck.Start(root);
            }
        }
    }

    // The gate: what must pass before a push. Each test project is built once (up to date, that is a couple of seconds), and then the tests, the compile of the app and the worker, the script checks and the
    // smell check all run at once, each writing to its own log. The slow, informational work (coverage, the browser tests, the full analyzer pass, the report) is a background run (BackgroundCheck).
    private static bool RunStages(string root, bool includeWebBuild, CheckTimings timings)
    {
        var projects = Config.TestProjects(root);
        foreach (var project in projects)
        {
            var build = timings.Begin("build: " + Path.GetFileName(project));
            if (!CheckTimings.Run(build, () => BuildTestProject(root, project, build)))
            {
                return false;
            }
        }

        var stages = new List<Task<bool>>();
        foreach (var project in projects)
        {
            var log = timings.Begin("tests: " + Path.GetFileName(project));
            stages.Add(Task.Run(() => CheckTimings.Run(log, () => RunTestProject(root, project, log))));
        }

        if (includeWebBuild)
        {
            var compile = timings.Begin("compile: " + string.Join(" + ", Config.CompileProjects.Select(Path.GetFileName)));
            stages.Add(Task.Run(() => CheckTimings.Run(compile, () => Config.CompileProjects.All(p => CompileProject(root, p, compile)))));
        }

        var scripts = timings.Begin("scripts: type check and lint");
        stages.Add(Task.Run(() => CheckTimings.Run(scripts, () => QualityChecks.Scripts(root, scripts))));
        if (includeWebBuild)
        {
            var smells = timings.Begin("code smells");
            stages.Add(Task.Run(() => CheckTimings.Run(smells, () => QualityChecks.Smells(root, smells))));
        }

        Task.WaitAll([.. stages]);
        var passed = stages.All(s => s.Result);
        if (passed)
        {
            Console.WriteLine("All checks passed.");
        }

        return passed;
    }

    private static bool BuildTestProject(string root, string project, StageLog log)
    {
        var (code, output) = Run("dotnet", $"build {project} --nologo -v q", root);
        if (code == 0)
        {
            return true;
        }

        log.Error(string.Join('\n', output.Split('\n').Where(l => l.Contains(" error ")).Distinct().Take(15)));
        log.Error($"{Path.GetFileName(project)} does not build - nothing was pushed.");
        return false;
    }

    private static bool RunTestProject(string root, string project, StageLog log)
    {
        log.WriteLine($"Running {Path.GetFileName(project)}...");
        // The counts go where the code-quality report reads them (reports/test-results/<project>/results.trx), replaced each run. The coverage (collected by the background run, not here) lives beside it and is left alone.
        var results = Path.Combine(root, "reports", "test-results", Path.GetFileName(project));
        Directory.CreateDirectory(results);
        File.Delete(Path.Combine(results, "results.trx"));

        var (code, output) = Run("dotnet", $"test {project} --no-build --nologo -v q --logger \"trx;LogFileName=results.trx\" --results-directory \"{results}\"", root);
        var summary = output.Split('\n').LastOrDefault(l => l.Contains("Passed!") || l.Contains("Failed!"))?.Trim();
        if (code != 0)
        {
            log.Error(string.Join('\n', output.Split('\n').Where(l => l.Contains("[FAIL]") || l.Contains("error") || l.Contains("Failed!") || l.Contains("budget") || l.Contains("Reads a") || l.Contains("without")).Take(30)));
            log.Error($"The {Path.GetFileName(project)} tests failed - nothing was pushed or built.");
            return false;
        }

        log.WriteLine("  " + (summary ?? "passed"));
        return true;
    }

    private static bool CompileProject(string root, string project, StageLog log)
    {
        var name = Path.GetFileName(project);
        log.WriteLine($"Compiling {name}...");
        var scratch = Path.Combine(Path.GetTempPath(), Config.App + "-check-build-" + name);
        var (buildCode, buildOutput) = Run("dotnet", $"build {project} --nologo -v q -o \"{scratch}\"", root);
        if (buildCode == 0)
        {
            return true;
        }

        log.Error(string.Join('\n', buildOutput.Split('\n').Where(l => l.Contains(" error ")).Distinct().Take(15)));
        log.Error($"The {name} does not compile - nothing was pushed.");
        return false;
    }

    // install-hooks: writes .git/hooks/pre-push. LF line endings matter: git runs it with sh, which chokes on CRLF.
    private static int InstallHooks()
    {
        var hook = string.Join('\n',
            "#!/bin/sh",
            "# Installed by: dotnet run --project tools/Deploy -- install-hooks   (skip once with: git push --no-verify)",
            "# Runs the tests and a compile of the web app before a push. Pushing only tags, or deleting a branch, is not checked.",
            "run=0",
            "while read local_ref local_sha remote_ref remote_sha; do",
            "  case \"$local_ref\" in",
            "    refs/tags/*) ;;",
            "    *) [ \"$local_sha\" != \"0000000000000000000000000000000000000000\" ] && run=1 ;;",
            "  esac",
            "done",
            "[ $run -eq 0 ] && exit 0",
            "exec dotnet run --project tools/Deploy -- check --background",
            "");
        var path = Path.Combine(RepoRoot(), ".git", "hooks", "pre-push");
        File.WriteAllText(path, hook, new System.Text.UTF8Encoding(false));
        Console.WriteLine("Installed " + path);
        return 0;
    }

    // minify <publish folder>: shrinks app.css and js/*.js inside a published copy (what "build" does automatically) - for trying a minified site locally.
    private static int MinifyCommand(Options options)
    {
        var root = options.Value("--dir") ?? throw new InvalidOperationException("minify needs --dir <a published folder that contains wwwroot>.");
        var (files, before, after, skipped) = Minifier.MinifyWebRoot(Path.Combine(root, "wwwroot"));
        Console.WriteLine($"Minified {files} files: {before / 1024.0:N1} KB -> {after / 1024.0:N1} KB");
        foreach (var line in skipped)
        {
            Console.WriteLine("  left as is: " + line);
        }

        return 0;
    }

    // notes: writes release-notes.json for the local site (the web project folder, gitignored) and optionally CHANGELOG-style markdown.
    private static int Notes(Options options)
    {
        var root = RepoRoot();
        var notes = ReleaseNotesCommand.Generate(root);
        var json = ReleaseNotesCommand.GenerateJson(root);
        File.WriteAllText(Path.Combine(root, Config.WebProject, "release-notes.json"), json);
        Console.WriteLine($"{notes.Releases.Count} release group(s), {notes.Releases.Sum(r => r.Commits.Count)} commits -> {Config.WebProject}/release-notes.json");
        if (options.Value("--markdown") is { } path)
        {
            File.WriteAllText(path, ReleaseNotesCommand.ToMarkdown(notes));
            Console.WriteLine("Markdown written to " + path);
        }

        return 0;
    }

    // backfill-tags: one-off. Reads production's past deploy times from Azure and tags the matching commits (dry run unless --apply).
    private static int BackfillTags(Options options)
    {
        var t = Resolve(options, "prod");
        var (code, output) = Run("cmd", $"/c az webapp log deployment list --name {t.Target.App} --resource-group {t.Target.Group} --query \"[].received_time\" -o tsv");
        if (code != 0)
        {
            return Fail("az failed: " + output);
        }

        var times = output.Split((char)10, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => DateTimeOffset.TryParse(l.Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTimeOffset?)null)
            .Where(at => at is not null).Select(at => at!.Value).ToList();
        Console.WriteLine($"{times.Count} past production deploys found in Azure.");
        return ReleaseNotesCommand.BackfillTags(RepoRoot(), times, options.Flag("--apply"));
    }

    // sbom: a CycloneDX bill of materials for the web project and what it pulls in, written to sbom/ (gitignored - a snapshot goes stale, so it is made on demand).
    private static int Sbom()
    {
        var root = RepoRoot();
        if (Run("cmd", "/c where dotnet-CycloneDX").Code != 0)
        {
            Console.Error.WriteLine("CycloneDX is not installed. Install it once with: dotnet tool install --global CycloneDX");
            return 1;
        }

        var (code, output) = Run("cmd", $"/c dotnet-CycloneDX {Config.WebProject} -o sbom --json", root);
        Console.WriteLine(code == 0 ? "Wrote sbom/bom.json (gitignored - regenerate on demand)." : output.Trim());
        return code;
    }

    internal static int Fail(string message)
    {
        Console.Error.WriteLine("ERROR: " + message);
        return 1;
    }
}
