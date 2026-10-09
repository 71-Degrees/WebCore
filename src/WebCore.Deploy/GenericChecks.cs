/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;

namespace WebCore.Deploy;

/// <summary>The verify checks every 71 Degrees web app gets, driven by <see cref="VerifySettings"/>: configured pages, static pages, discoverability files, version stamps, the WebSocket echo, headers and closed pages.</summary>
internal static class GenericChecks
{
    public static async Task<IReadOnlyList<VerifyOutcome>> RunAsync(VerifyContext context, VerifySettings settings)
    {
        var outcomes = new List<VerifyOutcome>();
        foreach (var page in settings.Pages)
        {
            outcomes.Add(await CheckPageAsync(context, page).ConfigureAwait(false));
        }

        outcomes.AddRange(await StaticPagesAsync(context, settings).ConfigureAwait(false));
        return outcomes;
    }

    /// <summary>The checks that come after the app's own: discoverability, scripts and styles, the WebSocket, headers and closed pages.</summary>
    public static async Task<IReadOnlyList<VerifyOutcome>> RunSiteChecksAsync(VerifyContext context, VerifySettings settings, Reply home)
    {
        var outcomes = new List<VerifyOutcome>();
        if (settings.SeoFiles)
        {
            outcomes.AddRange(await SeoFilesAsync(context).ConfigureAwait(false));
        }

        outcomes.Add(await AssetsAsync(context, home.Body).ConfigureAwait(false));
        if (!string.IsNullOrWhiteSpace(settings.WebSocketPath))
        {
            outcomes.Add(await WebSocketAsync(context.BaseUrl, settings.WebSocketPath).ConfigureAwait(false));
        }

        outcomes.Add(home.Headers.Contains("content-security-policy")
            ? new(VerifyResult.Pass, "security headers", "Content-Security-Policy is set")
            : new(VerifyResult.Fail, "security headers", "the home page has no Content-Security-Policy header"));
        foreach (var path in settings.ClosedPages)
        {
            outcomes.Add(await ClosedPageAsync(context, path).ConfigureAwait(false));
        }

        return outcomes;
    }

    private static async Task<VerifyOutcome> CheckPageAsync(VerifyContext context, PageCheck page)
    {
        var reply = await context.GetAsync(page.Path).ConfigureAwait(false);
        var name = page.Name ?? page.Path;
        var missing = page.Contains.Where(text => !reply.Body.Contains(text, StringComparison.Ordinal)).ToList();
        var unwanted = page.NotContains.Where(text => reply.Body.Contains(text, StringComparison.Ordinal)).ToList();
        if (reply.Status != page.Status)
        {
            return new(VerifyResult.Fail, name, $"status {reply.Status}, expected {page.Status}");
        }

        if (missing.Count > 0 || unwanted.Count > 0)
        {
            return new(VerifyResult.Fail, name, (missing.Count > 0 ? "missing: " + string.Join(", ", missing) : string.Empty) + (unwanted.Count > 0 ? " unexpected: " + string.Join(", ", unwanted) : string.Empty));
        }

        return reply.Body.Length < page.MinBytes
            ? new(VerifyResult.Fail, name, $"only {reply.Body.Length:N0} bytes (expected at least {page.MinBytes:N0})")
            : new(VerifyResult.Pass, name, page.Contains.Length > 0 ? "answers " + page.Status + " with the expected content" : page.Status.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // The information pages must stay plain HTML with no live connection (each connection costs memory and counts against the plan's WebSocket limit);
    // an interactive page must still get one. A page given an @onclick or marked interactive by mistake would silently put a connection back on every visitor.
    private static async Task<IReadOnlyList<VerifyOutcome>> StaticPagesAsync(VerifyContext context, VerifySettings settings)
    {
        if (settings.StaticPages.Length == 0 && settings.InteractivePages.Length == 0)
        {
            return Array.Empty<VerifyOutcome>();
        }

        var live = new List<string>();
        foreach (var path in settings.StaticPages)
        {
            if ((await context.GetAsync(path).ConfigureAwait(false)).Body.Contains(settings.InteractiveMarker, StringComparison.Ordinal))
            {
                live.Add(path);
            }
        }

        var notInteractive = new List<string>();
        foreach (var path in settings.InteractivePages)
        {
            if (!(await context.GetAsync(path).ConfigureAwait(false)).Body.Contains(settings.InteractiveMarker, StringComparison.Ordinal))
            {
                notInteractive.Add(path);
            }
        }

        if (live.Count > 0)
        {
            return new[] { new VerifyOutcome(VerifyResult.Fail, "information pages are static", "these pages open a live connection: " + string.Join(", ", live)) };
        }

        return notInteractive.Count > 0
            ? new[] { new VerifyOutcome(VerifyResult.Fail, "information pages are static", string.Join(", ", notInteractive) + " no longer render for a live connection") }
            : new[] { new VerifyOutcome(VerifyResult.Pass, "information pages are static", $"no live connection on {settings.StaticPages.Length} information page(s); {settings.InteractivePages.Length} interactive page(s) still have one") };
    }

    // Discoverability files: production is open to search engines, staging must keep them out.
    private static async Task<IReadOnlyList<VerifyOutcome>> SeoFilesAsync(VerifyContext context)
    {
        var robots = await context.GetAsync("/robots.txt").ConfigureAwait(false);
        if (context.TargetName != "prod")
        {
            return new[] { Expect("robots.txt keeps crawlers out", robots, 200, b => Regex.IsMatch(b, @"Disallow:\s*/\s*$", RegexOptions.Multiline), "disallows everything") };
        }

        var sitemap = await context.GetAsync("/sitemap.xml").ConfigureAwait(false);
        var llms = await context.GetAsync("/llms.txt").ConfigureAwait(false);
        return new[]
        {
            Expect("sitemap.xml", sitemap, 200, b => b.Contains("<urlset", StringComparison.Ordinal), "answers 200 and is a sitemap"),
            Expect("robots.txt", robots, 200, b => b.Contains("Sitemap", StringComparison.OrdinalIgnoreCase), "answers 200 and points at the sitemap"),
            llms.Status == 200 ? new VerifyOutcome(VerifyResult.Pass, "llms.txt", "200") : new VerifyOutcome(VerifyResult.Warn, "llms.txt", "status " + llms.Status),
        };
    }

    public static VerifyOutcome Expect(string name, Reply reply, int status, Func<string, bool> contains, string passDetail) =>
        reply.Status == status && contains(reply.Body)
            ? new(VerifyResult.Pass, name, passDetail)
            : new(VerifyResult.Fail, name, $"status {reply.Status}; the expected content {(contains(reply.Body) ? "was" : "was not")} found");

    private static async Task<VerifyOutcome> ClosedPageAsync(VerifyContext context, string path)
    {
        var page = await context.GetAsync(path).ConfigureAwait(false);
        var redirected = page.Status is 301 or 302 or 303 or 307 or 308 or 401 or 403;
        var asksToLogIn = page.Status == 200 && page.Body.Contains("Log in", StringComparison.Ordinal);
        return redirected || asksToLogIn
            ? new(VerifyResult.Pass, $"{path} is closed", redirected ? $"status {page.Status}" : "shows a log-in prompt")
            : new(VerifyResult.Fail, $"{path} is closed", $"a logged-out visitor got status {page.Status} and no log-in prompt");
    }

    // The WebSocket echo works through this site's own front end (IIS and the HTTP/2 setting included).
    private static async Task<VerifyOutcome> WebSocketAsync(string baseUrl, string path)
    {
        try
        {
            using var socket = new ClientWebSocket();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await socket.ConnectAsync(new Uri(baseUrl.Replace("https://", "wss://", StringComparison.Ordinal).Replace("http://", "ws://", StringComparison.Ordinal) + path), timeout.Token).ConfigureAwait(false);
            await socket.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
            var buffer = new byte[64];
            var received = await socket.ReceiveAsync(buffer, timeout.Token).ConfigureAwait(false);
            var echoed = Encoding.UTF8.GetString(buffer, 0, received.Count);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
            return echoed == "ping"
                ? new(VerifyResult.Pass, "WebSocket connection", $"opens and echoes ({clock.ElapsedMilliseconds} ms including the connection)")
                : new(VerifyResult.Fail, "WebSocket connection", $"connected, but the echo was \"{echoed}\"");
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException or InvalidOperationException)
        {
            return new(VerifyResult.Fail, "WebSocket connection", $"could not open or use {path}: " + ex.Message.Split('\n')[0]);
        }
    }

    private static async Task<VerifyOutcome> AssetsAsync(VerifyContext context, string html)
    {
        // Only files we version ourselves (StaticAssetVersion); the framework's own scripts carry their own fingerprint in the file name.
        var paths = Regex.Matches(html, "<link[^>]*rel=\"stylesheet\"[^>]*href=\"([^\"]+)\"|<script[^>]*src=\"([^\"]+)\"")
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value))
            .Where(p => !p.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !p.TrimStart('/').StartsWith('_'))
            .Distinct()
            .ToList();
        if (paths.Count == 0)
        {
            return new(VerifyResult.Fail, "scripts and styles", "no local scripts or stylesheets found on the home page");
        }

        var broken = new List<string>();
        foreach (var path in paths)
        {
            var asset = await context.GetAsync("/" + path.TrimStart('/')).ConfigureAwait(false);
            if (asset.Status != 200)
            {
                broken.Add($"{path} ({asset.Status})");
            }
        }

        var unstamped = paths.Where(p => !p.Contains("?v=", StringComparison.Ordinal)).ToList();
        if (broken.Count > 0)
        {
            return new(VerifyResult.Fail, "scripts and styles", "did not load: " + string.Join(", ", broken));
        }

        return unstamped.Count > 0
            ? new(VerifyResult.Fail, "scripts and styles", "no version stamp (?v=) on: " + string.Join(", ", unstamped))
            : new(VerifyResult.Pass, "scripts and styles", $"{paths.Count} files, all stamped and loading");
    }
}
