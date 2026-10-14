/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

// The shared deploy tool: builds, deploys and announces. An app runs it from a small launcher project (tools/Deploy) that calls DeployApp.RunAsync; what differs per app lives in deploy/deploy.json.
// It will not restart a server with a game in progress, promotes the exact build that was tested on staging, keeps the previous build for a one-command rollback, and can announce downtime.
//
// Usage (from the app's repo root, "deploy" standing for: dotnet run --project tools/Deploy --):
//   deploy status <stage|prod>
//   deploy build
//   deploy stage [--wait [minutes]] [--interrupt N] [--offline-page]   # build (or --zip) and deploy to staging
//   deploy prod  [--wait [minutes]] [--interrupt N]                     # promote the build that is on staging
//   deploy rollback <stage|prod>
//   deploy notice <stage|prod> --at "2026-10-03 22:00" [--duration 10] --message "..." [--notify] [--dry-run] [--test-to me@x.com]
//   deploy notice-clear <stage|prod>
//   deploy check [--tests-only] [--force]   # the tests + a compile of the web app (what the pre-push hook runs); skipped when the code is what was already checked (docs-only change, or a retry), --force runs it anyway
//   deploy minify --dir <published folder>   # css/js minification, as the build does (--no-minify on stage/prod skips it)
//   deploy verify <stage|prod> [--expect <build>] [--minutes N]   # smoke test of a live site: page content, version stamps, closed pages, the app's own checks, errors since deploy (also runs after stage/prod; --no-verify skips)
//   deploy perf <stage|prod> [--samples 8] [--strict]   # times the key pages, checks static-file caching, compares with the previous build (also runs after stage/prod)
//   deploy report <prod> [--days 7]                      # production telemetry per build: response times, storage calls per request
//   deploy install-hooks                                 # installs the pre-push hook that runs check; --no-verify skips it once
//   deploy setting <stage|prod> Name[=Value]             # read or change one Azure App Setting (restarts the site: waits for live games, then verifies)
//   deploy console [--port 5098] [--no-open]             # a local page of one-click buttons for the commands above
//   deploy sbom                                          # a CycloneDX software bill of materials for what ships -> sbom/bom.json (needs: dotnet tool install --global CycloneDX)
// Building for stage/prod runs the tests first; --skip-tests overrides.
//
// Options for any command: --url <base url> --api-key <key> override the target's address and key (to try it against a local server);
// --whatif on stage/prod/rollback does everything except the actual Azure deploy.

using System.Text.Json;

namespace WebCore.Deploy;

/// <summary>The entry point an app's launcher calls: <c>return await DeployApp.RunAsync(args, extensions);</c></summary>
public static class DeployApp
{
    public static async Task<int> RunAsync(string[] args, DeployExtensions? extensions = null)
    {
        try
        {
            return await DeployTool.RunAsync(args, extensions ?? new DeployExtensions()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or IOException)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }
}

internal sealed record DeployedBuild(string Version, string Zip, DateTimeOffset At);

internal sealed class DeployState
{
    public Dictionary<string, DeployedBuild> Current { get; set; } = new();

    public Dictionary<string, DeployedBuild> Previous { get; set; } = new();
}

internal sealed record MaintenanceRequest(DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string? Message, bool Notify, bool DryRun, string? OnlyTo);

internal static partial class DeployTool
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly Lazy<DeployConfig> Loaded = new(() => DeployConfig.Load(RepoRoot()));

    /// <summary>The app's settings from deploy/deploy.json, read once.</summary>
    internal static DeployConfig Config => Loaded.Value;

    internal static async Task<int> RunAsync(string[] args, DeployExtensions extensions)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Commands: " + string.Join(", ", Commands().Keys) + ". See the header of DeployTool.cs in the WebCore deploy project.");
            return 2;
        }

        var command = args[0].ToLowerInvariant();
        var options = new Options(args.Skip(1).ToArray(), extensions);
        return Commands().TryGetValue(command, out var run) ? await run(options) : Fail($"Unknown command '{command}'.");
    }

    // One row per command: adding a command is a line here, not another arm in a growing switch.
    private static Dictionary<string, Func<Options, Task<int>>> Commands() => new()
    {
        ["status"] = o => StatusAsync(o.Target("status")),
        ["build"] = o => Task.FromResult(Build(o) is null ? 1 : 0),
        ["stage"] = o => DeployAsync("stage", o),
        ["prod"] = o => DeployAsync("prod", o),
        ["rollback"] = RollbackAsync,
        ["notice"] = NoticeAsync,
        ["notice-clear"] = NoticeClearAsync,
        ["notes"] = o => Task.FromResult(Notes(o)),
        ["verify"] = o => VerifyCommands.VerifyAsync(o.Target("verify").Name, o.Target("verify").Target, o),
        ["perf"] = o => PerfCommands.PerfAsync(o.Target("perf").Name, o.Target("perf").Target, o),
        ["report"] = o => Task.FromResult(PerfCommands.Report(o.Target("report").Name, o.Target("report").Target, o)),
        ["minify"] = o => Task.FromResult(MinifyCommand(o)),
        ["check"] = o => Task.FromResult(Check(o) ? 0 : 1),
        ["install-hooks"] = _ => Task.FromResult(InstallHooks()),
        ["background-check"] = BackgroundCheck.RunAsync,
        ["setting"] = SettingCommand.RunAsync,
        ["console"] = ConsoleCommand.RunAsync,
        ["backfill-tags"] = o => Task.FromResult(BackfillTags(o)),
        ["sbom"] = _ => Task.FromResult(Sbom()),
        ["alerts"] = AlertsAsync,
    };

    // ---- targets ---------------------------------------------------------------------------------------------------------------------

    internal static (string Name, Target Target, Options Options) Resolve(Options options, string name)
    {
        if (!Config.Targets.TryGetValue(name, out var target))
        {
            throw new InvalidOperationException($"Unknown target '{name}' (expected one of: {string.Join(", ", Config.Targets.Keys)}).");
        }

        var url = options.Value("--url") ?? target.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException($"The '{name}' target has no address yet - fill it in deploy/deploy.json (or pass --url).");
        }

        return (name, target with { Url = url.TrimEnd('/') }, options);
    }
}
