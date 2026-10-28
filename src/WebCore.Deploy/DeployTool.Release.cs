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
    // ---- deploy ----------------------------------------------------------------------------------------------------------------------

    private static async Task<int> DeployAsync(string name, Options options)
    {
        var t = Resolve(options, name);
        var state = LoadState();
        var choice = await ChooseBuildAsync(name, options, state);
        if (choice.Failure is { } failed)
        {
            return failed;
        }

        var (zip, version) = (choice.Zip, choice.Version);
        if (!File.Exists(zip))
        {
            return Fail("Build file not found: " + zip);
        }

        // Never restart a server with people in a game.
        if (!await WaitUntilSafeAsync(t, options))
        {
            return 3;
        }

        if (options.Flag("--whatif"))
        {
            Console.WriteLine($"--whatif: would deploy {version} ({zip}) to {(string.IsNullOrWhiteSpace(t.Target.App) ? t.Target.Url : t.Target.App)}. Nothing changed.");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(t.Target.App))
        {
            return Fail($"The '{name}' target has no Azure app name yet - fill it in deploy/deploy.json.");
        }

        return await PushAsync(t, state, zip, version, isRollback: false);
    }

    /// <summary>What a deploy will ship: a build file given on the command line, the build tested on staging (for production), or a fresh build. <see cref="Failure"/> is the exit code when none could be chosen.</summary>
    private readonly record struct BuildChoice(int? Failure, string Zip = "", string Version = "");

    private static async Task<BuildChoice> ChooseBuildAsync(string name, Options options, DeployState state)
    {
        if (options.Value("--zip") is { } explicitZip)
        {
            return new BuildChoice(null, explicitZip, options.Value("--version") ?? Path.GetFileNameWithoutExtension(explicitZip));
        }

        if (name == "prod" && !options.Flag("--build"))
        {
            return await PromoteStagingBuildAsync(options, state);
        }

        return Build(options) is { } built ? new BuildChoice(null, built.Zip, built.Version) : new BuildChoice(1);
    }

    // Promote: ship exactly what is on staging, so what was tested is what goes live.
    private static async Task<BuildChoice> PromoteStagingBuildAsync(Options options, DeployState state)
    {
        if (!state.Current.TryGetValue("stage", out var staged))
        {
            return new BuildChoice(Fail("Nothing has been deployed to staging from this machine yet. Run 'stage' first (or use --build to build and ship straight to prod)."));
        }

        var live = await GetVersionAsync(Resolve(options, "stage").Target.Url);
        if (live != staged.Version && !options.Flag("--force-untested"))
        {
            return new BuildChoice(Fail($"Staging is running {live ?? "(unreachable)"} but the last build deployed there was {staged.Version}. Test the build you are promoting (or pass --force-untested)."));
        }

        Console.WriteLine($"Promoting build {staged.Version}, currently on staging.");
        return new BuildChoice(null, staged.Zip, staged.Version);
    }

    internal static async Task<bool> WaitUntilSafeAsync((string Name, Target Target, Options Options) t, Options options)
    {
        if (!Config.LiveSessions)
        {
            return true;   // this app has nothing live a restart could interrupt
        }

        var deadline = DateTimeOffset.UtcNow.AddMinutes(options.WaitMinutes());
        var confirmed = options.IntValue("--interrupt");
        while (true)
        {
            var status = await GetStatusAsync(t);
            if (status is null)
            {
                if (!options.Flag("--no-status-check"))
                {
                    Console.Error.WriteLine($"{t.Name} runs a build that predates the status endpoint, so live games can't be checked. If you know nobody is playing, pass --no-status-check.");
                    return false;
                }

                Console.WriteLine("Skipping the live-game check (--no-status-check).");
                return true;
            }

            var (allowed, message) = DeployGuard.Evaluate(status, confirmed);
            Console.WriteLine(message);
            if (allowed)
            {
                return true;
            }

            if (!options.Flag("--wait") || DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            Console.WriteLine("Waiting 30s for the games to finish...");
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }

    // While the site restarts, visitors get a friendly "back in a minute" page instead of raw errors: the page ships inside the zip as
    // app_offline.htm (IIS then serves it and keeps the app stopped), and is deleted once the upload is done so the app starts.
    // Opt-in (--offline-page) because the gain is modest and uneven. Measured on staging: a plain zip deploy gave visitors ~4 raw 503s/hangs over
    // ~18s; with the page, one run showed the friendly page for ~10s then one slow request, another only ~1s of it before the same restart blip.
    // The restart after the files land is what still shows raw errors, and only a slot swap removes that.
    private static string WithOfflinePage(string zip, (string Name, Target Target, Options Options) t)
    {
        var copy = Path.Combine(StateDir(), "deploying.zip");
        File.Copy(zip, copy, overwrite: true);
        using var archive = ZipFile.Open(copy, ZipArchiveMode.Update);
        var written = PageFromNoticeAsync(archive, t).GetAwaiter().GetResult();
        using var entry = archive.CreateEntry("app_offline.htm").Open();
        if (written is not null)
        {
            using var writer = new StreamWriter(entry, new System.Text.UTF8Encoding(false));
            writer.Write(written);
            return copy;
        }

        using var page = typeof(DeployTool).Assembly.GetManifestResourceStream("app_offline.htm") ?? throw new InvalidOperationException("The 'being updated' page is missing from the deploy tool.");
        page.CopyTo(entry);
        return copy;
    }

    // The page is drawn from the downtime notice on the site (heading, message, picture, the release notes of the build in this zip). Anything missing - no notice, no key, a site that does not answer - falls back to the plain page.
    private static async Task<string?> PageFromNoticeAsync(ZipArchive archive, (string Name, Target Target, Options Options) t)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var key = ApiKey(t);
            using var noticeResponse = await http.SendAsync(Request(HttpMethod.Get, $"{t.Target.Url}/api/admin/maintenance", key));
            var json = await noticeResponse.Content.ReadAsStringAsync();
            if (!noticeResponse.IsSuccessStatusCode || json.Contains("\"notice\":null", StringComparison.Ordinal))
            {
                return null;
            }

            using var document = System.Text.Json.JsonDocument.Parse(json);
            var image = document.RootElement.TryGetProperty("notice", out var notice) && notice.TryGetProperty("imageName", out var name) && name.GetString() is { Length: > 0 } file
                ? await ImageDataUriAsync(http, $"{t.Target.Url}/images/{Uri.EscapeDataString(file)}")
                : null;
            return OfflinePage.Render(OfflinePage.FromNotice(json, image, ReadReleaseNotes(archive)) ?? throw new InvalidOperationException("no notice"));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or TaskCanceledException)
        {
            Console.WriteLine($"  (the 'being updated' page is the plain one: {ex.Message})");
            return null;
        }
    }

    private static async Task<string?> ImageDataUriAsync(HttpClient http, string url)
    {
        using var response = await http.GetAsync(url);
        var bytes = response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync() : [];
        return bytes.Length is > 0 and < 400_000 ? $"data:{response.Content.Headers.ContentType?.MediaType ?? "image/png"};base64,{Convert.ToBase64String(bytes)}" : null;
    }

    private static readonly System.Text.Json.JsonSerializerOptions NotesJson = new() { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static ReleaseNotes? ReadReleaseNotes(ZipArchive archive)
    {
        var entry = archive.GetEntry("release-notes.json");
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        return System.Text.Json.JsonSerializer.Deserialize<ReleaseNotes>(stream, NotesJson);
    }

    private static bool RemoveOfflinePage(Target target)
    {
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var (code, output) = Run("cmd", $"/c az rest --method delete --url https://{target.App}.scm.azurewebsites.net/api/vfs/site/wwwroot/app_offline.htm --resource https://management.azure.com/ --headers If-Match=*");
            // Not found means the deploy did not leave it behind: also fine.
            if (code == 0 || output.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            Thread.Sleep(TimeSpan.FromSeconds(3 * attempt));
        }

        return false;
    }

    private static Task<int> PushAsync((string Name, Target Target, Options Options) t, DeployState state, string zip, string version, bool isRollback) =>
        QuietAroundAsync(t.Name, t.Target, t.Options, () => PushCoreAsync(t, state, zip, version, isRollback));

    private static async Task<int> PushCoreAsync((string Name, Target Target, Options Options) t, DeployState state, string zip, string version, bool isRollback)
    {
        if (Upload(t, zip, version) is { } failed)
        {
            return failed;
        }

        Console.WriteLine("Uploaded. Waiting for the new build to answer...");
        if (!await WaitForBuildAsync(t.Target, version))
        {
            Console.Error.WriteLine($"The new build did not report version {version} within 5 minutes. Check {t.Target.Url}{Config.Endpoints.Version} and the Azure log stream; 'rollback {t.Name}' restores the previous build.");
            await RestoreStagingAfterDeadBuildAsync(t, state, isRollback);
            return 5;
        }

        if (state.Current.TryGetValue(t.Name, out var before) && before.Version != version)
        {
            state.Previous[t.Name] = before;
        }

        state.Current[t.Name] = new DeployedBuild(version, zip, DateTimeOffset.UtcNow);
        SaveState(state);
        await AfterDeployAsync(t, version, isRollback);
        return 0;
    }

    /// <summary>Staging only: a build that never answers leaves nobody to use the site, so the build that was running (still recorded as current, because the failed one was never saved) goes back at once. Production is never touched without a person.</summary>
    private static async Task RestoreStagingAfterDeadBuildAsync((string Name, Target Target, Options Options) t, DeployState state, bool isRollback)
    {
        if (!state.Current.TryGetValue(t.Name, out var running) || !ShouldRestoreAfterDeadBuild(t.Name, isRollback, File.Exists(running.Zip)))
        {
            return;
        }

        Console.Error.WriteLine($"Putting staging back on {running.Version}, the build that was running before this deploy.");
        var restored = await PushAsync(t, state, running.Zip, running.Version, isRollback: true);
        Console.Error.WriteLine(restored == 0 ? "Staging is back on the previous build." : $"The automatic restore failed (exit {restored}); staging needs 'rollback stage' by hand.");
    }

    /// <summary>Only staging, only after a forward deploy (never after a rollback, which would loop), and only when the build to go back to is still on disk.</summary>
    internal static bool ShouldRestoreAfterDeadBuild(string target, bool isRollback, bool previousBuildOnDisk) => !isRollback && target == "stage" && previousBuildOnDisk;

    /// <summary>Uploads the build to Azure. Returns null when it went up, otherwise the exit code: 4 when the upload failed, 6 when the "being updated" page could not be taken down afterwards.</summary>
    private static int? Upload((string Name, Target Target, Options Options) t, string zip, string version)
    {
        var useOfflinePage = t.Options.Flag("--offline-page");
        var toUpload = useOfflinePage ? WithOfflinePage(zip, t) : zip;
        Console.WriteLine($"Deploying {version} to {t.Target.App}{(useOfflinePage ? " (visitors see the 'being updated' page meanwhile)" : string.Empty)}...");
        var (code, output) = Run("cmd", $"/c az webapp deploy --name {t.Target.App} --resource-group {t.Target.Group} --src-path \"{toUpload}\" --type zip");
        if (useOfflinePage)
        {
            File.Delete(toUpload);
        }

        if (code != 0)
        {
            Console.Error.WriteLine("az webapp deploy failed: " + output);
            if (useOfflinePage && !RemoveOfflinePage(t.Target))
            {
                Console.Error.WriteLine($"WARNING: the 'being updated' page may still be up on {t.Target.Url}. Delete site/wwwroot/app_offline.htm in Kudu ({t.Target.App}.scm.azurewebsites.net) to bring the site back.");
            }

            return 4;
        }

        if (useOfflinePage && !RemoveOfflinePage(t.Target))
        {
            Console.Error.WriteLine($"ERROR: the upload succeeded but the 'being updated' page could not be removed, so the site is still showing it. Delete site/wwwroot/app_offline.htm in Kudu ({t.Target.App}.scm.azurewebsites.net/DebugConsole) NOW.");
            return 6;
        }

        return null;
    }

    /// <summary>Waits up to five minutes for the site to report the build that was just uploaded.</summary>
    private static async Task<bool> WaitForBuildAsync(Target target, string version)
    {
        var started = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow < started.AddMinutes(5))
        {
            if (await GetVersionAsync(target.Url) == version)
            {
                Console.WriteLine($"Build {version} is live at {target.Url} (answered {(DateTimeOffset.UtcNow - started).TotalSeconds:N0}s after upload).");
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        return false;
    }

    // Tag and announce a production deploy, then time the key pages and read what the live site says (both report-only here; `verify` itself exits non-zero on a failure).
    private static async Task AfterDeployAsync((string Name, Target Target, Options Options) t, string version, bool isRollback)
    {
        if (t.Name == "prod" && !isRollback)
        {
            ReleaseNotesCommand.TagDeployedBuild(RepoRoot(), version.Split('+').Last(), DateTimeOffset.UtcNow);
            await SubmitToIndexNowAsync(RepoRoot(), t.Target.Url);
        }

        if (!t.Options.Flag("--no-perf"))
        {
            Console.WriteLine();
            await PerfCommands.PerfAsync(t.Name, t.Target, t.Options, expectedVersion: version);
        }

        if (!t.Options.Flag("--no-verify"))
        {
            Console.WriteLine();
            await VerifyCommands.VerifyAsync(t.Name, t.Target, t.Options, afterDeploy: true);
        }
    }

    // Tells Bing, Yandex and the other IndexNow search engines which pages to re-crawl: submits every URL in the live sitemap. The key
    // (the key named in deploy.json, normally Seo:IndexNowKey in appsettings.json) is public by design - the site serves it at /<key>.txt to prove it owns it. Best effort:
    // a failure here never fails a deploy.
    private static async Task SubmitToIndexNowAsync(string repoRoot, string siteUrl)
    {
        try
        {
            if (Config.IndexNow is not { AppSettings.Length: > 0 } indexNow)
            {
                return;
            }

            using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repoRoot, indexNow.AppSettings)));
            var key = ReadSetting(settings.RootElement, indexNow.Key);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            var sitemap = await Http.GetStringAsync(siteUrl.TrimEnd('/') + "/sitemap.xml");
            var urls = System.Text.RegularExpressions.Regex.Matches(sitemap, "<loc>(.*?)</loc>").Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)).ToArray();
            if (urls.Length == 0)
            {
                return;
            }

            var host = new Uri(siteUrl).Host;
            using var response = await Http.PostAsJsonAsync("https://api.indexnow.org/indexnow", new { host, key, keyLocation = $"{siteUrl.TrimEnd('/')}/{key}.txt", urlList = urls });
            Console.WriteLine($"IndexNow: submitted {urls.Length} URL(s) - HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"IndexNow: skipped ({ex.Message}).");
        }
    }

    private static async Task<int> RollbackAsync(Options options)
    {
        var t = Resolve(options, options.Target("rollback").Name);
        var state = LoadState();
        if (!state.Previous.TryGetValue(t.Name, out var previous) || !File.Exists(previous.Zip))
        {
            return Fail($"No previous {t.Name} build is recorded on this machine.");
        }

        Console.WriteLine($"Rolling {t.Name} back to {previous.Version}.");
        if (!await WaitUntilSafeAsync(t, options))
        {
            return 3;
        }

        if (options.Flag("--whatif"))
        {
            Console.WriteLine("--whatif: nothing changed.");
            return 0;
        }

        return await PushAsync(t, state, previous.Zip, previous.Version, isRollback: true);
    }

    /// <summary>A value from appsettings.json by its configuration key, with sections separated by colons ("Seo:IndexNowKey").</summary>
    private static string? ReadSetting(JsonElement root, string key)
    {
        var node = root;
        foreach (var part in key.Split(':'))
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(part, out node))
            {
                return null;
            }
        }

        return node.ValueKind == JsonValueKind.String ? node.GetString() : null;
    }
}
