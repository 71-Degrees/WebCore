/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Net;

namespace WebCore.Deploy.Tests;

public class GenericChecksTests
{
    private sealed class FakeSite(Dictionary<string, (HttpStatusCode Status, string Body)> pages) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var (status, body) = pages.TryGetValue(path, out var page) ? page : (HttpStatusCode.NotFound, string.Empty);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static VerifyContext Site(params (string Path, HttpStatusCode Status, string Body)[] pages) =>
        new(new HttpClient(new FakeSite(pages.ToDictionary(p => p.Path, p => (p.Status, p.Body)))), "https://site.test", "stage");

    [Fact]
    public async Task APageThatAnswersAsExpectedPasses()
    {
        var context = Site(("/about", HttpStatusCode.OK, "<h1>About Sample</h1>"));
        var settings = new VerifySettings { Pages = new[] { new PageCheck { Path = "/about", Contains = new[] { "About Sample" } } } };

        var outcomes = await GenericChecks.RunAsync(context, settings);

        Assert.Equal(VerifyResult.Pass, Assert.Single(outcomes).Result);
    }

    [Fact]
    public async Task AMissingPageAWrongStatusAndMissingTextAllFail()
    {
        var context = Site(("/a", HttpStatusCode.OK, "nothing here"), ("/b", HttpStatusCode.InternalServerError, "x"));
        var settings = new VerifySettings
        {
            Pages = new[] { new PageCheck { Path = "/a", Contains = new[] { "Welcome" } }, new PageCheck { Path = "/b" }, new PageCheck { Path = "/missing" } },
        };

        var outcomes = await GenericChecks.RunAsync(context, settings);

        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal(VerifyResult.Fail, o.Result));
    }

    [Fact]
    public async Task AnInformationPageThatOpensALiveConnectionFails()
    {
        var context = Site(("/terms", HttpStatusCode.OK, "<!--Blazor:{\"type\":\"server\"}-->"), ("/app", HttpStatusCode.OK, "<!--Blazor:{\"type\":\"server\"}-->"));
        var settings = new VerifySettings { StaticPages = new[] { "/terms" }, InteractivePages = new[] { "/app" } };

        var outcome = Assert.Single(await GenericChecks.RunAsync(context, settings));

        Assert.Equal(VerifyResult.Fail, outcome.Result);
        Assert.Contains("/terms", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosedPagesMustRedirectOrAskToLogIn()
    {
        var home = new Reply(200, "<link rel=\"stylesheet\" href=\"/app.css?v=1\">", new HashSet<string> { "content-security-policy" }, null);
        var context = Site(("/admin", HttpStatusCode.OK, "Welcome, admin"), ("/team", HttpStatusCode.OK, "Please Log in"), ("/app.css", HttpStatusCode.OK, "a{}"));
        var settings = new VerifySettings { ClosedPages = new[] { "/admin", "/team" }, SeoFiles = false };

        var outcomes = await GenericChecks.RunSiteChecksAsync(context, settings, home);

        Assert.Equal(VerifyResult.Fail, outcomes.Single(o => o.Name == "/admin is closed").Result);
        Assert.Equal(VerifyResult.Pass, outcomes.Single(o => o.Name == "/team is closed").Result);
        Assert.Equal(VerifyResult.Pass, outcomes.Single(o => o.Name == "security headers").Result);
        Assert.Equal(VerifyResult.Pass, outcomes.Single(o => o.Name == "scripts and styles").Result);
    }
}
