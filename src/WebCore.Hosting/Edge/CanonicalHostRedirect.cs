/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using Microsoft.AspNetCore.Http;

namespace WebCore.Hosting.Edge;

/// <summary>The parts of a request the canonical-host rule reads.</summary>
public sealed record CanonicalRequest(string? Host, string Method, string Path, string Query, bool IsWebSocketUpgrade);

/// <summary>
/// Once the site has a public address, a page request that arrives on any other host name (the bare domain, the azurewebsites.net name) is sent to the same page
/// on the public address, so shared links, bookmarks and printed QR codes all end up in one place. Lifted from Better Trivia and CrateDigger, which each had a copy.
/// </summary>
public static class CanonicalHostRedirect
{
    /// <summary>Health checks, the deploy tool's version probe and APIs answer on whatever name they are pointed at.</summary>
    public static readonly IReadOnlyList<string> DefaultExemptPaths = ["/healthz", "/version", "/api"];

    /// <summary>The address to redirect to, or null when the request should be served where it is.</summary>
    public static string? TargetFor(string? publicUrl, CanonicalRequest request, IReadOnlyList<string>? exemptPaths = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(publicUrl) || string.IsNullOrWhiteSpace(request.Host) || request.IsWebSocketUpgrade
            || !Uri.TryCreate(publicUrl, UriKind.Absolute, out var canonical))
        {
            return null;
        }

        // Only GET and HEAD move: a POST would lose its body.
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return null;
        }

        var path = new PathString(string.IsNullOrEmpty(request.Path) ? "/" : request.Path);
        if ((exemptPaths ?? DefaultExemptPaths).Any(exempt => path.StartsWithSegments(exempt, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var host = StripDefaultPort(request.Host.Trim());
        var bare = host.Contains(':', StringComparison.Ordinal) ? host[..host.LastIndexOf(':')] : host;
        if (bare is "localhost" or "127.0.0.1" or "[::1]" || host.Equals(canonical.Authority, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return canonical.GetLeftPart(UriPartial.Authority) + path.Value + request.Query;
    }

    private static string StripDefaultPort(string host) =>
        host.EndsWith(":443", StringComparison.Ordinal) ? host[..^4] : host.EndsWith(":80", StringComparison.Ordinal) ? host[..^3] : host;
}

/// <summary>
/// A HEAD request is a GET without the body. Routing matches only the methods an endpoint declares (GET), so a HEAD for a page answered 404 and uptime monitors
/// reported the site down. For pages (a path with a file extension goes to the static files, which answer HEAD themselves) HEAD runs as GET with the body thrown
/// away, then the method is put back. It must run before routing.
/// </summary>
public sealed class HeadAsGetMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!HttpMethods.IsHead(context.Request.Method) || Path.HasExtension(context.Request.Path.Value) || context.WebSockets.IsWebSocketRequest)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var body = context.Response.Body;
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = Stream.Null;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Request.Method = HttpMethods.Head;
            context.Response.Body = body;
        }
    }
}
