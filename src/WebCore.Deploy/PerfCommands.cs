/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebCore.Deploy;

internal sealed record PageTiming(string Path, int Status, double P50, double P95, double Max);

internal sealed record PerfRun(string Version, DateTimeOffset At, List<PageTiming> Pages);

/// <summary>
/// Performance measuring on a regular cadence (see docs/performance.md): <c>perf</c> times the key pages and checks what browsers are told about the static files, then
/// compares with the previous run of a different build; <c>report</c> reads production's own telemetry per release. Both are report-only unless <c>--strict</c>.
/// </summary>
internal static class PerfCommands
{


    // A page is "slower" than the last build's run only if it grew by half AND by a noticeable amount - a 10 ms page doubling is noise.
    private const double RegressionFactor = 1.5;
    private const double RegressionMinimumMs = 75;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    // ---- perf ---------------------------------------------------------------------------------------------------------------------------

    public static async Task<int> PerfAsync(string name, Target target, Options options, string? expectedVersion = null, int attempt = 0)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None }) { Timeout = TimeSpan.FromSeconds(30) };
        var baseUrl = target.Url.TrimEnd('/');
        var samples = Math.Clamp(options.IntValue("--samples") ?? 8, 3, 50);
        var problems = new List<string>();

        // Straight after a deploy the site is still restarting and answers a few 503s; timing that would report a problem that is gone in seconds.
        // When the deploy tool knows which build it just shipped, wait for exactly that build, running steadily.
        var startedAt = await WaitUntilSettled(http, baseUrl, expectedVersion);

        var version = await VersionAsync(http, baseUrl) ?? "(unknown)";
        Console.WriteLine($"Performance of {name} ({baseUrl}), build {version}, {samples} timed requests per page after one warm-up:");
        var timings = await TimePagesAsync(http, baseUrl, samples, problems);
        await CheckStaticAssets(http, baseUrl, problems);

        // A deploy can restart the site a second time after the new build first answers. If the process changed while we were timing, those numbers include the
        // restart's 503s and cold start: measure once more, and if it happens again say so rather than blame the code.
        if (startedAt is not null && await StartedAtAsync(http, baseUrl) != startedAt)
        {
            if (attempt == 0)
            {
                Console.WriteLine("  The site restarted while it was being timed, so those numbers are not meaningful - measuring again.");
                Console.WriteLine();
                return await PerfAsync(name, target, options, expectedVersion, attempt: 1);
            }

            problems.Add("the site restarted again while it was being timed - re-run 'perf' once it has settled");
        }

        var history = LoadHistory(name);
        var regressions = PrintComparison(timings, history.LastOrDefault(r => r.Version != version));

        // Keep one run per build (the latest), the last ten builds.
        history.RemoveAll(r => r.Version == version);
        history.Add(new PerfRun(version, DateTimeOffset.UtcNow, timings));
        SaveHistory(name, history.TakeLast(10).ToList());
        return Conclude(problems, regressions, options.Flag("--strict"));
    }

    /// <summary>Times each configured page: one warm-up request (the first after a deploy pays for cold code), then the samples. A page that does not answer 200 is a problem.</summary>
    private static async Task<List<PageTiming>> TimePagesAsync(HttpClient http, string baseUrl, int samples, List<string> problems)
    {
        var timings = new List<PageTiming>();
        foreach (var path in DeployTool.Config.PerfPages)
        {
            await SafeGet(http, baseUrl + path);
            var times = new List<double>();
            var status = 0;
            for (var i = 0; i < samples; i++)
            {
                var (code, ms) = await SafeGet(http, baseUrl + path);
                status = code;
                times.Add(ms);
            }

            times.Sort();
            timings.Add(new PageTiming(path, status, Percentile(times, 0.5), Percentile(times, 0.95), times[^1]));
            if (status != 200)
            {
                problems.Add($"{path} answered {status}");
            }
        }

        return timings;
    }

    /// <summary>Prints the timings beside the previous build's and returns the pages that got noticeably slower.</summary>
    private static List<string> PrintComparison(IReadOnlyList<PageTiming> timings, PerfRun? previous)
    {
        Console.WriteLine($"  {"page",-14} {"status",6} {"p50 ms",8} {"p95 ms",8} {"max ms",8}   vs {(previous is null ? "(no earlier build recorded)" : previous.Version)}");
        var regressions = new List<string>();
        foreach (var t in timings)
        {
            var note = string.Empty;
            if (previous?.Pages.FirstOrDefault(p => p.Path == t.Path) is { } before)
            {
                var delta = t.P50 - before.P50;
                note = $"{(delta >= 0 ? "+" : string.Empty)}{delta:N0} ms";
                if (t.P50 > before.P50 * RegressionFactor && delta > RegressionMinimumMs)
                {
                    note += "  <-- SLOWER";
                    regressions.Add($"{t.Path}: p50 {before.P50:N0} -> {t.P50:N0} ms");
                }
            }

            Console.WriteLine($"  {t.Path,-14} {t.Status,6} {t.P50,8:N0} {t.P95,8:N0} {t.Max,8:N0}   {note}");
        }

        return regressions;
    }

    private static int Conclude(List<string> problems, List<string> regressions, bool strict)
    {
        foreach (var problem in problems)
        {
            Console.WriteLine("  PROBLEM: " + problem);
        }

        foreach (var regression in regressions)
        {
            Console.WriteLine("  SLOWER than the previous build: " + regression);
        }

        if (problems.Count == 0 && regressions.Count == 0)
        {
            Console.WriteLine("  No problems, no slowdowns against the previous build.");
        }

        return problems.Count > 0 || (regressions.Count > 0 && strict) ? 6 : 0;
    }

    /// <summary>
    /// Waits (up to 2 minutes) until the site is steady: the home page and the health check answer 200, the version is the expected build (when one is given), and
    /// the process has not restarted (its start time is unchanged) for four checks in a row, about twelve seconds. Returns the process start time it settled on.
    /// A new build can first answer from its initial start and then restart once more as the deploy finishes, so "answers 200" alone is not enough.
    /// </summary>
    internal static async Task<string?> WaitUntilSettled(HttpClient http, string baseUrl, string? expectedVersion = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(120);
        var clean = 0;
        string? startedAt = null;
        while (DateTimeOffset.UtcNow < deadline && clean < 4)
        {
            var (version, started) = await VersionInfoAsync(http, baseUrl);
            var (home, _) = await SafeGet(http, baseUrl + "/");
            var (health, _) = await SafeGet(http, baseUrl + DeployTool.Config.Endpoints.Health);
            var steady = started is not null && (startedAt is null || started == startedAt);
            var rightBuild = expectedVersion is null || version == expectedVersion;
            clean = home == 200 && health == 200 && rightBuild && steady ? clean + 1 : 0;
            startedAt = started;
            if (clean < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }

        return startedAt;
    }

    private static async Task<string?> StartedAtAsync(HttpClient http, string baseUrl) => (await VersionInfoAsync(http, baseUrl)).StartedAt;

    private static async Task<(string? Version, string? StartedAt)> VersionInfoAsync(HttpClient http, string baseUrl)
    {
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync(baseUrl + DeployTool.Config.Endpoints.Version));
            var root = doc.RootElement;
            return (root.GetProperty("version").GetString(), root.TryGetProperty("startedAt", out var s) ? s.ToString() : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return (null, null);
        }
    }

    private static async Task<(int Status, double Ms)> SafeGet(HttpClient http, string url)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var response = await http.GetAsync(url);
            await response.Content.ReadAsByteArrayAsync();
            return ((int)response.StatusCode, clock.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (0, clock.Elapsed.TotalMilliseconds);
        }
    }

    private static async Task<string?> VersionAsync(HttpClient http, string baseUrl)
    {
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync(baseUrl + DeployTool.Config.Endpoints.Version));
            return doc.RootElement.GetProperty("version").GetString();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static double Percentile(List<double> sorted, double p) => sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

    /// <summary>The site's own stylesheets and scripts the home page links that carry a version: a ?v= stamp or a MapStaticAssets fingerprint (<see cref="GenericChecks.IsVersioned"/>).</summary>
    internal static List<string> VersionedAssets(string html) =>
        Regex.Matches(html, @"(?:href|src)=""([^""]+\.(?:css|js)(?:\?[^""]*)?)""")
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value))
            .Where(p => !p.StartsWith("http", StringComparison.OrdinalIgnoreCase) && GenericChecks.IsVersioned(p))
            .Distinct()
            .ToList();

    /// <summary>The hashed stylesheet and scripts the home page links must be kept for a year and sent Brotli-compressed; fonts and images for a month.</summary>
    private static async Task CheckStaticAssets(HttpClient http, string baseUrl, List<string> problems)
    {
        string html;
        try
        {
            html = await http.GetStringAsync(baseUrl + "/");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            problems.Add("could not read the home page to find its scripts");
            return;
        }

        var assets = VersionedAssets(html);
        if (assets.Count == 0)
        {
            problems.Add("the home page links no versioned css/js (?v=... or a fingerprinted name)");
            return;
        }

        var immutableOk = 0;
        var brotliOk = 0;
        foreach (var asset in assets)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/{asset.TrimStart('/')}");
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "br");
            try
            {
                using var response = await http.SendAsync(request);
                if (response.Headers.CacheControl?.ToString().Contains("immutable") == true)
                {
                    immutableOk++;
                }
                else
                {
                    problems.Add($"{asset} is not marked immutable");
                }

                if (response.Content.Headers.ContentEncoding.Contains("br"))
                {
                    brotliOk++;
                }
                else
                {
                    problems.Add($"{asset} is not sent Brotli-compressed");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                problems.Add($"{asset} could not be fetched");
            }
        }

        Console.WriteLine($"  static assets: {immutableOk}/{assets.Count} immutable, {brotliOk}/{assets.Count} Brotli");
        using var font = await http.GetAsync($"{baseUrl}/fonts/plus-jakarta-sans.woff2");
        if (FontProblem(font.StatusCode, font.Headers.CacheControl?.MaxAge) is { } problem)
        {
            problems.Add(problem);
        }
    }

    /// <summary>The site's web font must be kept for at least a week. A site that has no such font (it answers 404; MyWave uses system fonts) has
    /// nothing to check - the missing file is not a caching problem.</summary>
    internal static string? FontProblem(System.Net.HttpStatusCode status, TimeSpan? maxAge) =>
        status == System.Net.HttpStatusCode.NotFound ? null
        : maxAge is null || maxAge < TimeSpan.FromDays(7) ? "fonts are not kept for at least a week"
        : null;

    private static string HistoryPath(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DeployTool.Config.App + "Deploy", $"perf-{name}.json");

    private static List<PerfRun> LoadHistory(string name)
    {
        var path = HistoryPath(name);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<PerfRun>>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static void SaveHistory(string name, List<PerfRun> runs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath(name))!);
        File.WriteAllText(HistoryPath(name), JsonSerializer.Serialize(runs, Json));
    }

    // ---- report (production telemetry per release) ----------------------------------------------------------------------------------------

    public static int Report(string name, Target target, Options options)
    {
        var workspace = target.Workspace ?? throw new InvalidOperationException($"The '{name}' target has no Log Analytics workspace id in tools/Deploy/targets.json.");
        var days = Math.Clamp(options.IntValue("--days") ?? 7, 1, 90);

        var requests = Query(workspace,
            $"AppRequests | where TimeGenerated > ago({days}d) | where Name !startswith 'Microsoft.AspNetCore.Components.Server' and Name !has '_blazor' and Name !has 'healthz' and Name != 'GET /version' " +
            "| summarize requests=count(), p50=round(percentile(DurationMs,50)), p95=round(percentile(DurationMs,95)), slow=countif(DurationMs>2000), lastSeen=max(TimeGenerated) by AppRoleName, AppVersion | order by lastSeen desc");
        var storage = Query(workspace,
            $"AppDependencies | where TimeGenerated > ago({days}d) and (Type has 'Blob' or Target has 'blob.core') | summarize storageCalls=count() by AppRoleName, AppVersion");

        var calls = storage.ToDictionary(r => (Role(r), Version(r)), r => (long)Num(r, "storageCalls"));
        Console.WriteLine($"{name}: request timing and storage calls per build, last {days} day(s) (page requests only; live-game connections, health checks and probes excluded)");
        Console.WriteLine($"  {"app",-26} {"build",-18} {"requests",9} {"p50 ms",7} {"p95 ms",7} {">2s",5} {"storage/req",12}");
        foreach (var row in requests)
        {
            var role = Role(row);
            var version = Version(row);
            var n = (long)Num(row, "requests");
            calls.TryGetValue((role, version), out var storageCalls);
            Console.WriteLine($"  {role,-26} {version,-18} {n,9} {Num(row, "p50"),7:N0} {Num(row, "p95"),7:N0} {(long)Num(row, "slow"),5} {(n == 0 ? 0 : (double)storageCalls / n),12:N2}");
        }

        if (requests.Count == 0)
        {
            Console.WriteLine("  (no telemetry in that window - builds before this feature carry no version tag)");
        }

        return 0;
    }

    // Log Analytics via az returns numbers as JSON numbers or as strings depending on the column; accept both.
    private static double Num(JsonElement row, string name)
    {
        var value = row.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static string Role(JsonElement row) => row.TryGetProperty("AppRoleName", out var r) ? r.GetString() ?? "" : "";

    private static string Version(JsonElement row) => row.TryGetProperty("AppVersion", out var v) && !string.IsNullOrEmpty(v.GetString()) ? v.GetString()! : "(untagged)";

    internal static List<JsonElement> Query(string workspace, string kql)
    {
        var (code, output) = DeployTool.Run("cmd", $"/c az monitor log-analytics query -w {workspace} --analytics-query \"{kql}\" -o json");
        if (code != 0)
        {
            throw new InvalidOperationException("az monitor log-analytics query failed: " + output.Trim());
        }

        var start = output.IndexOf('[');
        using var doc = JsonDocument.Parse(output[start..]);
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }
}
