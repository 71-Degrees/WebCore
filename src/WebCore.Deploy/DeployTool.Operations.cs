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
    private static bool Check(Options options) => RunChecks(RepoRoot(), includeWebBuild: !options.Flag("--tests-only"));

    private static bool RunChecks(string root, bool includeWebBuild)
    {
        var timings = new CheckTimings();
        try
        {
            return RunStages(root, includeWebBuild, timings);
        }
        finally
        {
            timings.Print(Console.Out);
            timings.Append(root);
        }
    }

    private static bool RunStages(string root, bool includeWebBuild, CheckTimings timings)
    {
        var clock = Stopwatch.StartNew();
        // The unit tests (Core, guards, contracts) and the web integration tests (the real app in memory: read budgets, cache headers), each timed on its own.
        foreach (var project in Config.TestProjects(root))
        {
            var name = Path.GetFileName(project);
            if (!timings.Measure("tests: " + name, () => RunTestProject(root, project)))
            {
                return false;
            }
        }

        if (includeWebBuild)
        {
            // The web app and the voice worker (its own project, built into its own image): a change to shared code can break one and not the other.
            foreach (var project in Config.CompileProjects)
            {
                if (!timings.Measure("compile: " + Path.GetFileName(project), () => CompileProject(root, project)))
                {
                    return false;
                }
            }
        }

        if (!timings.Measure("scripts: type check and lint", () => QualityChecks.Scripts(root)))
        {
            return false;
        }

        if (includeWebBuild && !timings.Measure("code smells", () => QualityChecks.Smells(root)))
        {
            return false;
        }

        Console.WriteLine($"All checks passed in {clock.Elapsed.TotalSeconds:N0}s.");
        return true;
    }

    private static bool RunTestProject(string root, string project)
    {
        Console.WriteLine($"Running {Path.GetFileName(project)}...");
        // The results (a TRX file and the coverage) go where the code-quality report reads them (reports/test-results/<project>), so its test counts and coverage are never more than a push old.
        // The folder is regenerated output, replaced each run: the coverage file lands in a new folder every time, and the old ones would pile up and be read as one.
        var results = Path.Combine(root, "reports", "test-results", Path.GetFileName(project));
        if (Directory.Exists(results))
        {
            Directory.Delete(results, recursive: true);
        }

        var (code, output) = Run("dotnet", $"test {project} --nologo -v q --collect:\"XPlat Code Coverage\" --logger \"trx;LogFileName=results.trx\" --results-directory \"{results}\"", root);
        var summary = output.Split('\n').LastOrDefault(l => l.Contains("Passed!") || l.Contains("Failed!"))?.Trim();
        if (code != 0)
        {
            Console.Error.WriteLine(string.Join('\n', output.Split('\n').Where(l => l.Contains("[FAIL]") || l.Contains("error") || l.Contains("Failed!") || l.Contains("budget") || l.Contains("Reads a") || l.Contains("without")).Take(30)));
            Console.Error.WriteLine("The tests failed - nothing was pushed or built.");
            return false;
        }

        Console.WriteLine("  " + (summary ?? "passed"));
        return true;
    }

    private static bool CompileProject(string root, string project)
    {
        var name = Path.GetFileName(project);
        Console.WriteLine($"Compiling {name}...");
        var scratch = Path.Combine(Path.GetTempPath(), Config.App + "-check-build-" + name);
        var (buildCode, buildOutput) = Run("dotnet", $"build {project} --nologo -v q -o \"{scratch}\"", root);
        if (buildCode == 0)
        {
            return true;
        }

        Console.Error.WriteLine(string.Join('\n', buildOutput.Split('\n').Where(l => l.Contains(" error ")).Distinct().Take(15)));
        Console.Error.WriteLine($"The {name} does not compile - nothing was pushed.");
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
            "exec dotnet run --project tools/Deploy -- check",
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
