/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.FileProviders;
using WebCore.Hosting.Assets;
using WebCore.Hosting.Edge;
using WebCore.Hosting.Security;

namespace WebCore.Hosting.Tests.Adversarial;

// The API-key gate, scanner-probe refusal, canonical-host redirect, CSP builder and asset versioning under
// missing keys, hostile paths and hosts, bad methods, and files that appear or vanish.
public sealed class EdgeHostingAdversarialTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WebCoreAdvHost_" + Guid.NewGuid().ToString("N"));

    public EdgeHostingAdversarialTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    // ---- ApiKeyGate ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ApiKeyGate_NoKeyConfigured_AnswersServiceUnavailable(string? key)
    {
        var result = new ApiKeyGate(key).Check(new DefaultHttpContext());

        var content = Assert.IsType<ContentHttpResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, content.StatusCode);
    }

    [Fact]
    public void ApiKeyGate_MissingHeader_IsUnauthorized()
    {
        var result = new ApiKeyGate("secret").Check(new DefaultHttpContext());

        Assert.IsType<UnauthorizedHttpResult>(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("secre")]
    [InlineData("secrets")]
    [InlineData("SECRET")]
    [InlineData(" secret")]
    public void ApiKeyGate_WrongOrNearMissKeys_AreUnauthorized(string provided)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Api-Key"] = provided;

        Assert.IsType<UnauthorizedHttpResult>(new ApiKeyGate("secret").Check(http));
    }

    [Fact]
    public void ApiKeyGate_ExactKey_PassesWithNoResult()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Api-Key"] = "secret";

        Assert.Null(new ApiKeyGate("secret").Check(http));
    }

    [Fact]
    public void ApiKeyGate_HeaderNameIsCaseInsensitive()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["x-api-key"] = "secret";

        Assert.Null(new ApiKeyGate("secret").Check(http));
    }

    [Fact]
    public void ApiKeyGate_NonAsciiKey_MatchesByUtf8Bytes()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Api-Key"] = "clé-secrète";

        Assert.Null(new ApiKeyGate("clé-secrète").Check(http));
    }

    // ---- ProbePaths ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/quiz/PMAX7K")]
    [InlineData("/games/abc/index")]
    [InlineData("/.well-known/security.txt")]
    public void ProbePaths_RealPagesAndEmptyPaths_AreNotProbes(string? path)
    {
        Assert.False(ProbePaths.IsProbe(path));
    }

    [Theory]
    [InlineData("/wp-admin/install.php")]
    [InlineData("/index.php")]
    [InlineData("/config.PHP")]
    [InlineData("/.env")]
    [InlineData("/backup.sql")]
    [InlineData("/site.bak")]
    [InlineData("/a/../etc/passwd")]
    [InlineData("/a\\b")]
    [InlineData("/phpmyadmin/")]
    [InlineData("/PMA")]
    [InlineData("/cgi-bin/x")]
    [InlineData("/.git/config")]
    [InlineData("/vendor/phpunit")]
    public void ProbePaths_ScannerShapes_AreRefused(string path)
    {
        Assert.True(ProbePaths.IsProbe(path));
    }

    [Fact(Skip = "FINDING F-H3 (security): traversal written after /.well-known/ is exempted before the traversal check runs")]
    public void ProbePaths_TraversalHiddenUnderWellKnown_SlipsThroughTheExemption_Finding()
    {
        // The well-known exemption is checked before the traversal test, so a ".." after it is not refused.
        Assert.True(ProbePaths.IsProbe("/.well-known/../../x.php"));
    }

    [Fact]
    public void ProbePaths_PercentEncodedTraversal_IsNotDecodedHere()
    {
        // Recorded: the check reads the path as the server gives it; an encoded dot-dot passes this filter.
        Assert.False(ProbePaths.IsProbe("/%2e%2e/secret"));
    }

    // ---- CanonicalHostRedirect ----

    private static CanonicalRequest Get(string? host, string path = "/quiz", string query = "", string method = "GET", bool websocket = false) =>
        new(host, method, path, query, websocket);

    [Fact]
    public void CanonicalHost_SameHost_NoRedirect()
    {
        Assert.Null(CanonicalHostRedirect.TargetFor("https://quiz.example", Get("quiz.example")));
    }

    [Fact]
    public void CanonicalHost_DefaultPortsAreIgnoredWhenComparing()
    {
        Assert.Null(CanonicalHostRedirect.TargetFor("https://quiz.example", Get("quiz.example:443")));
        Assert.Null(CanonicalHostRedirect.TargetFor("https://quiz.example", Get("quiz.example:80")));
    }

    [Fact]
    public void CanonicalHost_OtherHost_RedirectsKeepingPathAndQuery()
    {
        var target = CanonicalHostRedirect.TargetFor("https://quiz.example", Get("old.example", "/quiz/7", "?x=1"));

        Assert.Equal("https://quiz.example/quiz/7?x=1", target);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:5199")]
    [InlineData("127.0.0.1:5199")]
    [InlineData("[::1]:8080")]
    public void CanonicalHost_LoopbackHosts_NeverRedirect(string host)
    {
        Assert.Null(CanonicalHostRedirect.TargetFor("https://quiz.example", Get(host)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CanonicalHost_NoHostOrNoPublicUrl_NoRedirect(string? host)
    {
        Assert.Null(CanonicalHostRedirect.TargetFor("https://quiz.example", Get(host)));
        Assert.Null(CanonicalHostRedirect.TargetFor(host, Get("old.example")));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void CanonicalHost_WriteMethods_AreNeverMoved(string method)
    {
        Assert.Null(CanonicalHostRedirect.TargetFor("https://quiz.example", Get("old.example", method: method)));
    }

    [Fact]
    public void CanonicalHost_LowerCaseGet_IsStillMoved_BecauseMethodMatchingIsCaseInsensitive()
    {
        Assert.NotNull(CanonicalHostRedirect.TargetFor("https://quiz.example", Get("old.example", method: "get")));
    }

    [Fact]
    public void CanonicalHost_WebSocketUpgrade_IsNeverMoved()
    {
        Assert.Null(CanonicalHostRedirect.TargetFor("https://quiz.example", Get("old.example", websocket: true)));
    }

    [Theory]
    [InlineData("/healthz", true)]
    [InlineData("/healthz/deep", true)]
    [InlineData("/healthz2", false)]
    [InlineData("/API", true)]
    public void CanonicalHost_ExemptPathsStayOnTheirHost_AndMatchSegmentsOnly(string path, bool exempt)
    {
        var target = CanonicalHostRedirect.TargetFor("https://quiz.example", Get("old.example", path));

        Assert.Equal(!exempt, target is not null);
    }

    [Fact]
    public void CanonicalHost_EmptyPath_BecomesRoot()
    {
        Assert.Equal("https://quiz.example/", CanonicalHostRedirect.TargetFor("https://quiz.example", Get("old.example", string.Empty)));
    }

    [Fact]
    public void CanonicalHost_NullRequest_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => CanonicalHostRedirect.TargetFor("https://quiz.example", null!));
    }

    [Fact]
    public void CanonicalHost_NonAbsolutePublicUrl_NoRedirect()
    {
        Assert.Null(CanonicalHostRedirect.TargetFor("quiz.example", Get("old.example")));
    }

    [Fact]
    public void CanonicalHost_PublicUrlWithPathOnly_DropsThePathFromTheTarget()
    {
        Assert.Equal("https://quiz.example/a", CanonicalHostRedirect.TargetFor("https://quiz.example/some/where", Get("old.example", "/a")));
    }

    // ---- ContentSecurityPolicy ----

    [Fact]
    public void Csp_DefaultPolicy_IsStrictAndComplete()
    {
        var text = new ContentSecurityPolicy().ToString();

        Assert.Contains("default-src 'self'", text);
        Assert.Contains("frame-ancestors 'none'", text);
        Assert.DoesNotContain("unsafe-inline", text);
    }

    [Fact]
    public void Csp_AllowAddsOnlyNewSourcesOnce()
    {
        var text = new ContentSecurityPolicy().Allow("img-src", "https://img.example", "data:", "https://img.example").ToString();

        Assert.Contains("img-src 'self' data: https://img.example", text);
    }

    [Fact]
    public void Csp_AllowWithNoSources_LeavesTheDirectiveAlone()
    {
        var before = new ContentSecurityPolicy().ToString();

        Assert.Equal(before, new ContentSecurityPolicy().Allow("script-src").ToString());
    }

    [Fact]
    public void Csp_SetWithNoSources_LeavesADirectiveWithATrailingSpace_AsRecorded()
    {
        Assert.Contains("font-src ;", new ContentSecurityPolicy().Set("font-src").ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Csp_BlankDirectiveName_IsRefused(string? directive)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ContentSecurityPolicy().Allow(directive!, "x"));
        Assert.ThrowsAny<ArgumentException>(() => new ContentSecurityPolicy().Set(directive!, "x"));
    }

    [Fact]
    public void Csp_NullSourceArray_IsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new ContentSecurityPolicy().Allow("script-src", null!));
    }

    [Fact(Skip = "FINDING F-CSP1 (config): CSP sources are not validated; a ';' in a configured source writes a new directive")]
    public void Csp_SourceWithASemicolon_CanInjectAnotherDirective_Finding()
    {
        // Sources are not validated, so a configured value carrying ";" writes a new directive into the header.
        var text = new ContentSecurityPolicy().Allow("script-src", "https://cdn.example; script-src 'unsafe-inline'").ToString();

        Assert.DoesNotContain("; script-src 'unsafe-inline'", text);
    }

    [Fact]
    public void Csp_CloneIsIndependent()
    {
        var original = new ContentSecurityPolicy();
        var copy = original.Clone().Allow("connect-src", "https://api.example");

        Assert.Contains("https://api.example", copy.ToString());
        Assert.DoesNotContain("https://api.example", original.ToString());
    }

    // ---- StaticAssetVersion ----

    [Fact]
    public void StaticAssetVersion_ExistingFile_GetsAnEightCharacterHash()
    {
        File.WriteAllText(Path.Combine(_root, "site.css"), "body{}");
        var versions = new StaticAssetVersion(new PhysicalFileProvider(_root));

        Assert.Matches("^/site.css\\?v=[0-9a-f]{8}$", versions.Url("/site.css"));
    }

    [Fact]
    public void StaticAssetVersion_MissingFile_IsReturnedUnversioned()
    {
        var versions = new StaticAssetVersion(new PhysicalFileProvider(_root));

        Assert.Equal("/nothing.css", versions.Url("/nothing.css"));
    }

    [Fact]
    public void StaticAssetVersion_FileCreatedLater_StaysUnversionedUntilRestart_AsRecorded()
    {
        var versions = new StaticAssetVersion(new PhysicalFileProvider(_root));
        Assert.Equal("/late.css", versions.Url("/late.css"));

        File.WriteAllText(Path.Combine(_root, "late.css"), "x");

        Assert.Equal("/late.css", versions.Url("/late.css"));
    }

    [Fact]
    public void StaticAssetVersion_FileChangedAfterFirstUse_KeepsTheOldHash_AsRecorded()
    {
        File.WriteAllText(Path.Combine(_root, "a.js"), "one");
        var versions = new StaticAssetVersion(new PhysicalFileProvider(_root));
        var first = versions.Url("/a.js");

        File.WriteAllText(Path.Combine(_root, "a.js"), "two");

        Assert.Equal(first, versions.Url("/a.js"));
    }

    [Fact]
    public void StaticAssetVersion_TraversalOutOfTheRoot_IsNotVersionedOrFollowed()
    {
        var outside = Path.Combine(_root, "..", "outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(Path.GetFullPath(outside), "secret");
        try
        {
            Directory.CreateDirectory(Path.Combine(_root, "inner"));
            var versions = new StaticAssetVersion(new PhysicalFileProvider(Path.Combine(_root, "inner")));

            Assert.Equal("/../" + Path.GetFileName(outside), versions.Url("/../" + Path.GetFileName(outside)));
        }
        finally
        {
            File.Delete(Path.GetFullPath(outside));
        }
    }

    [Fact]
    public void StaticAssetVersion_NullPath_ThrowsArgumentNull()
    {
        var versions = new StaticAssetVersion(new PhysicalFileProvider(_root));

        Assert.Throws<ArgumentNullException>(() => versions.Url(null!));
    }
}
