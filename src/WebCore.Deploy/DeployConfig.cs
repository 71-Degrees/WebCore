/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Text.Json;

namespace WebCore.Deploy;

/// <summary>A deployment target: the Azure app, where it lives, its public address, the environment variable holding its API key, and (optionally) its Log Analytics workspace.</summary>
public sealed record Target(string App, string Group, string Url, string KeyEnv, string? Workspace = null);

/// <summary>The addresses of the app's own endpoints the deploy tool talks to.</summary>
public sealed class EndpointSettings
{
    public string Version { get; init; } = "/version";

    public string Health { get; init; } = "/healthz";

    public string Status { get; init; } = "/api/admin/status";

    public string Maintenance { get; init; } = "/api/admin/maintenance";
}

/// <summary>Where the public site's IndexNow key lives, so a production deploy can tell search engines what to re-crawl.</summary>
public sealed class IndexNowSettings
{
    public string AppSettings { get; init; } = string.Empty;

    public string Key { get; init; } = "Seo:IndexNowKey";
}

/// <summary>A page the verify step loads and judges: its status, text it must (or must not) contain, and a least size.</summary>
public sealed class PageCheck
{
    public string Path { get; init; } = "/";

    public string? Name { get; init; }

    public int Status { get; init; } = 200;

    public string[] Contains { get; init; } = Array.Empty<string>();

    public string[] NotContains { get; init; } = Array.Empty<string>();

    public int MinBytes { get; init; }
}

/// <summary>What the generic verify checks look at. The app's own special checks come in through <see cref="DeployExtensions"/>.</summary>
public sealed class VerifySettings
{
    /// <summary>The page checks, in order. The home page (<c>/</c>) is always loaded for the asset and header checks.</summary>
    public PageCheck[] Pages { get; init; } = Array.Empty<PageCheck>();

    /// <summary>Pages that must stay plain HTML with no live connection (each connection costs memory).</summary>
    public string[] StaticPages { get; init; } = Array.Empty<string>();

    /// <summary>Pages that must still render for a live connection, proving the marker check is not simply failing to find anything.</summary>
    public string[] InteractivePages { get; init; } = Array.Empty<string>();

    /// <summary>The text that appears in a page rendered for a live connection (Blazor Server marks it in a comment).</summary>
    public string InteractiveMarker { get; init; } = "\"type\":\"server\"";

    /// <summary>Pages a logged-out visitor must not be able to use: each redirects or shows a "Log in" prompt.</summary>
    public string[] ClosedPages { get; init; } = Array.Empty<string>();

    /// <summary>The WebSocket echo endpoint, if the app has one; checked through the site's own front end.</summary>
    public string? WebSocketPath { get; init; }

    /// <summary>Production must serve robots.txt, sitemap.xml and llms.txt; staging must keep crawlers out.</summary>
    public bool SeoFiles { get; init; } = true;
}

/// <summary>Quieting the "site is down" alert while a deploy restarts the site on purpose (an Azure Monitor alert processing rule that ends by itself).</summary>
public sealed class AlertQuietSettings
{
    /// <summary>The targets whose restarts are quieted (usually only production, which is the one with alerts).</summary>
    public string[] Targets { get; init; } = { "prod" };

    /// <summary>The names of the alert rules to mute (for example the availability alert).</summary>
    public string[] Rules { get; init; } = Array.Empty<string>();

    /// <summary>How long the quiet window is while the deploy runs; it ends by itself even if the tool dies.</summary>
    public int QuietMinutes { get; init; } = 25;

    /// <summary>Once the new build is steady, the window is cut to this many minutes: long enough for the availability samples that failed during the restart to leave the alert's evaluation window.</summary>
    public int SettleMinutes { get; init; } = 11;

    /// <summary>The processing rule's name. Default: &lt;app&gt;-deploy-quiet.</summary>
    public string? RuleName { get; init; }
}

/// <summary>An extra button on the one-click console: the deploy command it runs, and the question to ask first when it changes something live.</summary>
public sealed class ConsoleActionSettings
{
    public string Id { get; init; } = string.Empty;

    public string Group { get; init; } = "Other";

    public string Label { get; init; } = string.Empty;

    public string[] Args { get; init; } = Array.Empty<string>();

    public string? Confirm { get; init; }

    /// <summary>Make one button per target (stage, prod), filling {target} and {live} in the id, label, arguments and question.</summary>
    public string[] Targets { get; init; } = Array.Empty<string>();
}

/// <summary>The slow checks that run after a push instead of during it, on a separate copy of the code (see BackgroundCheck).</summary>
public sealed class BackgroundSettings
{
    /// <summary>Commands to run in the copy, in order, through cmd. Placeholders: {main} (the real working folder), {reports} (its reports folder), {commit}.</summary>
    public string[] Commands { get; init; } = Array.Empty<string>();

    /// <summary>A command run last, whatever the others did: it builds the report and shows it.</summary>
    public string? Report { get; init; }

    /// <summary>Environment variables for the commands (the same placeholders apply): for example where their outputs go.</summary>
    public Dictionary<string, string> Env { get; init; } = new();

    /// <summary>Folders linked from the working folder into the copy rather than copied (node_modules).</summary>
    public string[] Link { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Everything that differs from one app to the next, read from <c>deploy/deploy.json</c> in the repository being deployed. Only <c>app</c>, <c>webProject</c> and <c>targets</c> are needed;
/// the rest default to what a standard 71 Degrees web app does.
/// </summary>
public sealed class DeployConfig
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The app's name; also names the folder (under LocalApplicationData, <c>&lt;App&gt;Deploy</c>) where builds and deploy state are kept.</summary>
    public string App { get; init; } = string.Empty;

    /// <summary>The web project's folder, relative to the repository root. It is published, and release-notes.json is written beside it.</summary>
    public string WebProject { get; init; } = string.Empty;

    public string Runtime { get; init; } = "win-x86";

    /// <summary>The test projects to run before a build. Default: every folder under tests/ that holds a project.</summary>
    public string[]? Tests { get; init; }

    /// <summary>The projects compiled by the pre-push check (the web app, a worker). Default: just the web project.</summary>
    public string[]? Compile { get; init; }

    /// <summary>A command that lints the browser scripts (for example an eslint call), or empty for none.</summary>
    public string? ScriptLint { get; init; }

    /// <summary>A command that type-checks the browser scripts, or empty for none.</summary>
    public string? ScriptTypeCheck { get; init; }

    public IndexNowSettings? IndexNow { get; init; }

    public EndpointSettings Endpoints { get; init; } = new();

    public Dictionary<string, Target> Targets { get; init; } = new();

    /// <summary>The pages the perf command times.</summary>
    public string[] PerfPages { get; init; } = { "/", "/healthz" };

    /// <summary>False for an app with no live sessions (no games, no calls): deploys then need no status endpoint and never wait for anyone.</summary>
    public bool LiveSessions { get; init; } = true;

    public VerifySettings Verify { get; init; } = new();

    /// <summary>Quiets the "site is down" alert during deploys and setting changes. Off when absent.</summary>
    public AlertQuietSettings? AlertQuiet { get; init; }

    /// <summary>Checks run after a push, away from the push (coverage, browser tests, the report). Off when absent.</summary>
    public BackgroundSettings? Background { get; init; }

    /// <summary>Extra console buttons, after the standard ones.</summary>
    public ConsoleActionSettings[] ConsoleActions { get; init; } = Array.Empty<ConsoleActionSettings>();

    public static string PathFor(string root) => Path.Combine(root, "deploy", "deploy.json");

    public static DeployConfig Load(string root)
    {
        var path = PathFor(root);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"No deploy settings: create {path} (see the WebCore README for the settings).");
        }

        var config = JsonSerializer.Deserialize<DeployConfig>(File.ReadAllText(path), Json) ?? new DeployConfig();
        return string.IsNullOrWhiteSpace(config.App) || string.IsNullOrWhiteSpace(config.WebProject)
            ? throw new InvalidOperationException($"{path} must set \"app\" and \"webProject\".")
            : config;
    }

    /// <summary>The test project folders to run: the configured ones, else every folder under tests/ with a project file.</summary>
    public IReadOnlyList<string> TestProjects(string root)
    {
        if (Tests is not null)
        {
            return Tests;
        }

        var folder = Path.Combine(root, "tests");
        return Directory.Exists(folder)
            ? Directory.EnumerateDirectories(folder).Where(d => Directory.EnumerateFiles(d, "*.csproj").Any()).Select(d => "tests/" + Path.GetFileName(d)).OrderBy(d => d, StringComparer.Ordinal).ToList()
            : Array.Empty<string>();
    }

    public IReadOnlyList<string> CompileProjects => Compile ?? new[] { WebProject };
}
