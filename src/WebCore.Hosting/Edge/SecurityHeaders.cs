/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace WebCore.Hosting.Edge;

/// <summary>What the security headers say for this site. The defaults are the strict ones; an app changes only what it needs, saying why where it does.</summary>
public sealed class SecurityHeadersOptions
{
    public ContentSecurityPolicy Csp { get; } = new();

    /// <summary>Which browser features this site may use. Everything off by default; turn one on for the site itself only, for example <c>camera=(self)</c>.</summary>
    public string PermissionsPolicy { get; set; } = "geolocation=(), camera=(), microphone=()";

    public string FrameOptions { get; set; } = "DENY";

    public string ReferrerPolicy { get; set; } = "strict-origin-when-cross-origin";

    /// <summary>True on a staging copy: every response tells search engines not to index or follow it.</summary>
    public bool NoIndexEverywhere { get; set; }

    /// <summary>Paths (and everything under them) that search engines must not index even on the live site: a shared link, an admin area.</summary>
    public IList<string> NoIndexPaths { get; } = [];

    /// <summary>
    /// A last word per request, after the defaults are set: the request, the response headers and a copy of the CSP to change. For a rule that depends on the page,
    /// such as letting the site frame one of its own pages.
    /// </summary>
    public Action<HttpContext, IHeaderDictionary, ContentSecurityPolicy>? PerRequest { get; set; }
}

/// <summary>
/// Sets the security headers on every response: no content sniffing, the frame and referrer rules, the permissions policy, the CSP and, where asked, noindex. The
/// policy string is built once; a per-request hook builds its own copy only when it is set.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, SecurityHeadersOptions options)
{
    private readonly string _csp = options.Csp.ToString();

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = options.FrameOptions;
        headers["Referrer-Policy"] = options.ReferrerPolicy;
        headers["Permissions-Policy"] = options.PermissionsPolicy;
        headers["Content-Security-Policy"] = _csp;
        if (options.NoIndexEverywhere || options.NoIndexPaths.Any(path => context.Request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase)))
        {
            headers["X-Robots-Tag"] = "noindex, nofollow";
        }

        if (options.PerRequest is { } adjust)
        {
            var csp = options.Csp.Clone();
            adjust(context, headers, csp);
            headers["Content-Security-Policy"] = csp.ToString();
        }

        await next(context).ConfigureAwait(false);
    }
}

/// <summary>The edge of the pipeline in a few calls: <c>UseSecurityHeaders</c>, <c>UseCanonicalHost</c>, <c>UseHeadAsGet</c>.</summary>
public static class EdgeMiddlewareExtensions
{
    /// <summary>Security headers on every response, starting strict; <paramref name="configure"/> adds what this site needs.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, Action<SecurityHeadersOptions>? configure = null)
    {
        var options = new SecurityHeadersOptions();
        configure?.Invoke(options);
        return app.UseMiddleware<SecurityHeadersMiddleware>(options);
    }

    /// <summary>
    /// With a public address, a page request on any other host name (the *.azurewebsites.net name, the bare domain) is redirected (308) to the same page there.
    /// Nothing happens when <paramref name="publicUrl"/> is empty, so an app can call this unconditionally.
    /// </summary>
    public static IApplicationBuilder UseCanonicalHost(this IApplicationBuilder app, string? publicUrl, IReadOnlyList<string>? exemptPaths = null)
    {
        if (string.IsNullOrWhiteSpace(publicUrl))
        {
            return app;
        }

        var exempt = exemptPaths ?? CanonicalHostRedirect.DefaultExemptPaths;
        return app.Use(async (context, next) =>
        {
            var target = CanonicalHostRedirect.TargetFor(publicUrl, new CanonicalRequest(
                context.Request.Host.Value, context.Request.Method, context.Request.Path.Value ?? "/", context.Request.QueryString.Value ?? string.Empty, context.WebSockets.IsWebSocketRequest), exempt);
            if (target is null)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
            context.Response.Headers.Location = target;
        });
    }

    /// <summary>A HEAD for a page answers like its GET without the body, so uptime monitors and link checkers see 200. Call before UseRouting (call UseRouting explicitly).</summary>
    public static IApplicationBuilder UseHeadAsGet(this IApplicationBuilder app) => app.UseMiddleware<HeadAsGetMiddleware>();
}
