/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using Microsoft.AspNetCore.Http;
using WebCore.Hosting.Edge;

namespace WebCore.Hosting.Tests;

public sealed class CanonicalHostRedirectTests
{
    private const string Public = "https://www.example.com/";

    private static string? Target(string? publicUrl, string host, string method = "GET", string path = "/", string query = "", bool ws = false) =>
        CanonicalHostRedirect.TargetFor(publicUrl, new CanonicalRequest(host, method, path, query, ws));

    [Theory]
    [InlineData("example.com", "/", "", "https://www.example.com/")]
    [InlineData("site-v9u4ia.azurewebsites.net", "/play/ABC123", "?v=1", "https://www.example.com/play/ABC123?v=1")]
    [InlineData("EXAMPLE.com:443", "/quizzes", "", "https://www.example.com/quizzes")]
    [InlineData("staging.example.com", "/demo/abc", "", "https://www.example.com/demo/abc")]
    public void OtherHosts_AreSentToTheSamePageOnThePublicAddress(string host, string path, string query, string expected) =>
        Assert.Equal(expected, Target(Public, host, path: path, query: query));

    [Theory]
    [InlineData("www.example.com")]
    [InlineData("WWW.Example.COM")]
    [InlineData("www.example.com:443")]
    public void ThePublicHostItself_IsNeverRedirected(string host) => Assert.Null(Target(Public, host));

    [Fact]
    public void NotConfigured_NeverRedirects()
    {
        Assert.Null(Target(null, "anything.azurewebsites.net"));
        Assert.Null(Target("", "anything.azurewebsites.net"));
        Assert.Null(Target("not a url", "anything.azurewebsites.net"));
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/version")]
    [InlineData("/api/admin/status")]
    [InlineData("/API/quizzes")]
    public void HealthChecksAndTheApi_AnswerOnAnyName(string path) => Assert.Null(Target(Public, "old.azurewebsites.net", path: path));

    [Fact]
    public void APathThatOnlyStartsLikeAnExemptOne_IsStillRedirected() => Assert.NotNull(Target(Public, "old.azurewebsites.net", path: "/apiary"));

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void OnlyGetAndHead_AreRedirected(string method) => Assert.Null(Target(Public, "old.azurewebsites.net", method: method));

    [Fact]
    public void Head_IsRedirectedLikeGet() => Assert.NotNull(Target(Public, "old.azurewebsites.net", method: "HEAD"));

    [Fact]
    public void LiveConnections_AreNeverRedirected() => Assert.Null(Target(Public, "old.azurewebsites.net", ws: true));

    [Theory]
    [InlineData("localhost:5199")]
    [InlineData("127.0.0.1:5199")]
    public void LocalDevelopment_IsNeverRedirected(string host) => Assert.Null(Target(Public, host));
}

public sealed class SecurityHeadersTests
{
    private static async Task<IHeaderDictionary> HeadersForAsync(SecurityHeadersOptions options, string path = "/")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        await new SecurityHeadersMiddleware(_ => Task.CompletedTask, options).InvokeAsync(context);
        return context.Response.Headers;
    }

    [Fact]
    public void TheStrictStart_AllowsOnlyTheSiteItself()
    {
        var csp = new ContentSecurityPolicy().ToString();

        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self';", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe", csp, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowedSources_AreAddedOnce_AndNewDirectivesAppear()
    {
        var csp = new ContentSecurityPolicy()
            .Allow("img-src", "https://tile.openstreetmap.org", "https://tile.openstreetmap.org")
            .Allow("frame-src", "https://www.youtube.com")
            .ToString();

        Assert.Contains("img-src 'self' data: https://tile.openstreetmap.org;", csp, StringComparison.Ordinal);
        Assert.Contains("frame-src https://www.youtube.com", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryResponse_GetsTheHeaders_AndNoIndexOnlyWhereAsked()
    {
        var options = new SecurityHeadersOptions { PermissionsPolicy = "camera=(self)" };
        options.NoIndexPaths.Add("/share");

        var page = await HeadersForAsync(options, "/collection");
        var shared = await HeadersForAsync(options, "/share/abc");

        Assert.Equal("nosniff", page["X-Content-Type-Options"]);
        Assert.Equal("DENY", page["X-Frame-Options"]);
        Assert.Equal("camera=(self)", page["Permissions-Policy"]);
        Assert.False(page.ContainsKey("X-Robots-Tag"));
        Assert.Equal("noindex, nofollow", shared["X-Robots-Tag"]);
    }

    [Fact]
    public async Task APerRequestRule_ChangesThatResponseOnly()
    {
        var options = new SecurityHeadersOptions
        {
            PerRequest = (context, headers, csp) =>
            {
                if (context.Request.Path.StartsWithSegments("/preview"))
                {
                    headers["X-Frame-Options"] = "SAMEORIGIN";
                    csp.Set("frame-ancestors", "'self'");
                }
            },
        };

        var preview = await HeadersForAsync(options, "/preview/1");
        var other = await HeadersForAsync(options, "/");

        Assert.Contains("frame-ancestors 'self'", preview["Content-Security-Policy"].ToString(), StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", other["Content-Security-Policy"].ToString(), StringComparison.Ordinal);
        Assert.Equal("SAMEORIGIN", preview["X-Frame-Options"]);
    }
}

public sealed class HeadAsGetTests
{
    [Fact]
    public async Task AHeadForAPage_RunsAsGet_WithNoBody_AndTheMethodPutBack()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Head;
        context.Request.Path = "/collection";
        var original = new MemoryStream();
        context.Response.Body = original;
        string? seen = null;

        await new HeadAsGetMiddleware(async ctx =>
        {
            seen = ctx.Request.Method;
            await ctx.Response.WriteAsync("page");
        }).InvokeAsync(context);

        Assert.Equal(HttpMethods.Get, seen);
        Assert.Equal(HttpMethods.Head, context.Request.Method);
        Assert.Equal(0, original.Length);
    }

    [Fact]
    public async Task AHeadForAFile_IsLeftToTheStaticFiles()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Head;
        context.Request.Path = "/app.css";
        string? seen = null;

        await new HeadAsGetMiddleware(ctx => { seen = ctx.Request.Method; return Task.CompletedTask; }).InvokeAsync(context);

        Assert.Equal(HttpMethods.Head, seen);
    }
}
