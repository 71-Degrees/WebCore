/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Text.Json;

namespace WebCore.Deploy;

/// <summary>
/// <c>verify &lt;stage|prod&gt;</c>: a smoke test of a live deployment that goes beyond "the pages answer 200" (that is what <c>perf</c> does). It reads what the pages actually say,
/// checks that scripts and styles carry their version stamps, that the pages that must be closed to a logged-out visitor are, runs the app's own checks, and - when the target has a
/// Log Analytics workspace - that the app has not logged exceptions since the deploy. It needs no password and writes nothing. FAIL means something is broken; WARN means look at it.
/// It also runs at the end of every <c>stage</c> and <c>prod</c> deploy (<c>--no-verify</c> skips it). See docs/release-checklist.md for what it cannot check.
/// </summary>
internal static class VerifyCommands
{
    /// <param name="afterDeploy">True when called at the end of a deploy: then wait for the expected build to be live and steady before checking. A standalone
    /// run checks the site as it is now, so a wrong <c>--expect</c> fails at once instead of waiting for a build that is not coming.</param>
    public static async Task<int> VerifyAsync(string name, Target target, Options options, bool afterDeploy = false)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        var baseUrl = target.Url.TrimEnd('/');
        var context = new VerifyContext(http, baseUrl, name, publicSite: name == DeployTool.Config.ReleaseTarget);
        var settings = DeployTool.Config.Verify;

        var expected = options.Value("--expect") ?? (DeployTool.LoadState().Current.TryGetValue(name, out var current) ? current.Version : null);
        await PerfCommands.WaitUntilSettled(http, baseUrl, afterDeploy ? expected : null);
        Console.WriteLine($"Verifying {name} ({baseUrl}):");

        var home = await context.GetAsync("/");
        var outcomes = new List<VerifyOutcome> { await CheckVersionAsync(context, expected) };
        outcomes.AddRange(await GenericChecks.RunAsync(context, settings));
        foreach (var check in options.Extensions.VerifyChecks)
        {
            outcomes.AddRange(await check.RunAsync(context));
        }

        outcomes.AddRange(await GenericChecks.RunSiteChecksAsync(context, settings, home));
        outcomes.Add(CheckLoggedErrors(name, target, options));
        await NotifyObserversAsync(options.Extensions.VerifyObservers, context, outcomes, afterDeploy).ConfigureAwait(false);
        return Report(name, outcomes);
    }

    /// <summary>Tells each observer the result. One that fails is noted and skipped: reporting the verify onward must never turn a good deploy into a failed one.</summary>
    internal static async Task NotifyObserversAsync(IEnumerable<IVerifyObserver> observers, VerifyContext context, IReadOnlyList<VerifyOutcome> outcomes, bool afterDeploy)
    {
        foreach (var observer in observers)
        {
            try
            {
                await observer.ObserveAsync(context, outcomes, afterDeploy).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
            {
                Console.WriteLine($"  NOTE  {observer.GetType().Name} could not report the result onward: {ex.Message}");
            }
        }
    }

    private static async Task<VerifyOutcome> CheckVersionAsync(VerifyContext context, string? expected)
    {
        var live = await context.GetAsync(DeployTool.Config.Endpoints.Version);
        var liveVersion = TryVersion(live.Body);
        if (liveVersion is null)
        {
            return new(VerifyResult.Fail, "build version", $"{DeployTool.Config.Endpoints.Version} did not answer with a version (status {live.Status})");
        }

        return expected is not null && !string.Equals(expected, liveVersion, StringComparison.Ordinal)
            ? new(VerifyResult.Fail, "build version", $"live is {liveVersion}, expected {expected}")
            : new(VerifyResult.Pass, "build version", liveVersion + (expected is null ? " (nothing recorded to compare with)" : " matches what was deployed"));
    }

    private static int Report(string name, IReadOnlyList<VerifyOutcome> outcomes)
    {
        Console.WriteLine();
        foreach (var o in outcomes)
        {
            Console.WriteLine($"  {(o.Result == VerifyResult.Pass ? "PASS" : o.Result == VerifyResult.Warn ? "WARN" : "FAIL")}  {o.Name,-38} {o.Detail}");
        }

        var failures = outcomes.Count(o => o.Result == VerifyResult.Fail);
        var warnings = outcomes.Count(o => o.Result == VerifyResult.Warn);
        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"Verify {name}: {outcomes.Count - warnings} passed{(warnings > 0 ? $", {warnings} to look at" : string.Empty)}."
            : $"Verify {name}: {failures} FAILED, {warnings} to look at. If this is production and the failure is real, 'rollback {name}' restores the previous build.");
        return failures == 0 ? 0 : 7;
    }

    /// <summary>Exceptions and error-level logs since the deploy, from Log Analytics. Warns rather than fails: a restart can log a shutdown exception, and the
    /// telemetry lags a few minutes, so a clean result right after a deploy is not proof - run <c>verify</c> again later.</summary>
    private static VerifyOutcome CheckLoggedErrors(string name, Target target, Options options)
    {
        if (target.Workspace is null)
        {
            return new(VerifyResult.Pass, "errors logged since deploy", "skipped - this target has no Log Analytics workspace");
        }

        var minutes = Math.Clamp(options.IntValue("--minutes") ?? SinceDeployMinutes(name), 5, 240);
        try
        {
            var rows = PerfCommands.Query(target.Workspace,
                $"union (AppExceptions | project TimeGenerated, AppRoleName, Kind='exception', Message=strcat(ExceptionType, ': ', substring(OuterMessage, 0, 100))), " +
                $"(AppTraces | where SeverityLevel >= 3 | project TimeGenerated, AppRoleName, Kind='error log', Message=substring(Message, 0, 100)) " +
                $"| where TimeGenerated > ago({minutes}m) and AppRoleName == '{target.App}' | summarize n=count() by Kind, Message | order by n desc | take 5");
            if (rows.Count == 0)
            {
                return new(VerifyResult.Pass, "errors logged since deploy", $"none in the last {minutes} minutes (telemetry lags a few minutes - run verify again later to be sure)");
            }

            var top = string.Join("; ", rows.Select(r => $"{r.GetProperty("n").ToString()}x {r.GetProperty("Kind").GetString()} {r.GetProperty("Message").GetString()}"));
            return new(VerifyResult.Warn, "errors logged since deploy", $"in the last {minutes} minutes: {top}");
        }
        catch (InvalidOperationException ex)
        {
            return new(VerifyResult.Warn, "errors logged since deploy", "could not query the logs (is 'az login' current?): " + ex.Message.Split('\n')[0]);
        }
    }

    private static int SinceDeployMinutes(string name) =>
        DeployTool.LoadState().Current.TryGetValue(name, out var build) ? (int)Math.Ceiling((DateTimeOffset.UtcNow - build.At).TotalMinutes) + 2 : 30;

    private static string? TryVersion(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("version").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

}
