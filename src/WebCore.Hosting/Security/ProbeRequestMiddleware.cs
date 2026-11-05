/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Hosting.Security;

/// <summary>
/// What an automated scanner asks for on any site it finds: WordPress and phpMyAdmin paths, PHP and ASP scripts, <c>.env</c> and <c>.git</c> files, path traversal. This site has none of them, so there is
/// nothing to find - and without this, every one of those requests was answered by re-running the whole page pipeline to render the "not found" page. They now get a bare, empty 404 straight away.
/// Real paths are never matched: the rules only recognise things this site does not have (and <c>/.well-known/</c>, which it may need, is explicitly allowed).
/// </summary>
public static class ProbePaths
{
    private static readonly string[] ScriptAndSecretExtensions = { ".php", ".php5", ".phtml", ".asp", ".aspx", ".jsp", ".jspx", ".cgi", ".env", ".sql", ".bak", ".old", ".swp" };

    private static readonly string[] ProbeSegmentPrefixes = { "wp-", "wordpress", "phpmyadmin", "pma", "phpinfo", "cgi-bin", "xmlrpc", "boaform", "hnap1", "vendor", "actuator", "jenkins", "solr", "administrator" };

    /// <summary>True when the path is one only a scanner would ask for.</summary>
    public static bool IsProbe(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith("/.well-known/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.Contains("..", StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal))
        {
            return true;   // path traversal
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return ScriptAndSecretExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))
            || segments.Any(s => s.StartsWith('.'))
            || (segments.Length > 0 && IsScannerTarget(segments[0]));
    }

    // Only the first segment is held against the scanner names: that is where a scanner asks for them (/wp-admin, /phpmyadmin, /vendor/...). Deeper segments are the site's own values -
    // a random game code such as PMAX7K, a slug, an id - and matching them 404'd a real page (about one game code in 32,768 starts with "PMA"; found 2026-11-05).
    private static bool IsScannerTarget(string firstSegment) =>
        ProbeSegmentPrefixes.Any(p => firstSegment.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Answers scanner requests (see <see cref="ProbePaths"/>) with an empty 404 before anything else runs.</summary>
public sealed class ProbeRequestMiddleware(RequestDelegate next, ILogger<ProbeRequestMiddleware> logger)
{
    private const int MaxLoggedPath = 100;

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (!ProbePaths.IsProbe(path))
        {
            return next(context);
        }

        logger.LogDebug("Refused a scanner request for {Path}", path is { Length: > MaxLoggedPath } ? path[..MaxLoggedPath] : path);
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentLength = 0;
        return Task.CompletedTask;
    }
}

/// <summary>Registers the scanner refusal.</summary>
public static class ProbeRequestMiddlewareExtensions
{
    /// <summary>Answers scanner requests with a bare 404 before the rest of the pipeline runs: <c>app.UseProbeRequestRefusal();</c> early, before static files and routing.</summary>
    public static IApplicationBuilder UseProbeRequestRefusal(this IApplicationBuilder app) => app.UseMiddleware<ProbeRequestMiddleware>();
}
