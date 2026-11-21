using System.Net;
using static WebCore.Hosting.Tests.Adversarial.EndpointSite;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WebCore.Accounts;
using WebCore.Audit;
using WebCore.Health;
using WebCore.Hosting.Accounts;
using WebCore.Hosting.Audit;
using WebCore.Hosting.Health;
using WebCore.Hosting.Security;
using WebCore.Storage;

namespace WebCore.Hosting.Tests.Adversarial;

// The health write door (signals and hosted reports) under missing, wrong and absent keys, malformed and oversized publications, hostile links and ids; the audit
// actor the trail records for a request; and the form gate itself (content type, a missing service). Every store is a temp folder.
public sealed class HealthAndAuditEndpointsAdversarialTests
{
    private const string Key = "secret";

    // Fixed once, so a test can replace this exact text inside a signal it built.
    private static readonly string Now = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");

    private static string Signal(string id = "quiz-checks", string extra = "") =>
        "{\"id\":\"" + id + "\",\"area\":\"Quizzes\",\"title\":\"Quiz checks\",\"status\":\"Green\",\"headline\":\"142 known good\",\"asOf\":\"" + Now + "\"" + extra + "}";

    private static string Publication(string signals, string environment = "production") =>
        "{\"environment\":\"" + environment + "\",\"signals\":[" + signals + "]}";

    private static async Task<EndpointSite> StartHealthAsync(string? key = Key)
    {
        var folder = EndpointSite.NewFolder();
        return await EndpointSite.StartPlainAsync(folder, builder =>
        {
            builder.Services.AddSingleton(new ApiKeyGate(key));
            builder.Services.AddHealthMonitoring(
                builder.Configuration,
                "production",
                _ => new FileDocumentStore(Path.Combine(folder, "health")),
                _ => new FileBinaryStore(Path.Combine(folder, "reports")));
        }, app => app.MapKeyedGroup().MapHealthSignals());
    }

    private static async Task<HttpResponseMessage> PutJsonAsync(EndpointSite site, string path, string json, string? apiKey = Key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (apiKey is not null)
        {
            request.Headers.Add("X-Api-Key", apiKey);
        }

        return await site.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PutBodyAsync(EndpointSite site, string path, HttpContent content, string apiKey = Key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = content };
        request.Headers.Add("X-Api-Key", apiKey);
        return await site.SendAsync(request);
    }

    private static HealthSignalStore Store(EndpointSite site) => site.Services.GetRequiredService<HealthSignalStore>();

    // A readable body that does not report its length, so the request goes out without a Content-Length (as a chunked upload does).
    private sealed class UnsizedStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---- Health: keys ----

    [Fact]
    public async Task Publish_WithTheKey_IsAccepted()
    {
        await using var site = await StartHealthAsync();

        var response = await PutJsonAsync(site, "/api/health/signals", Publication(Signal()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"accepted\":1", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Publish_WithoutTheKey_IsUnauthorized()
    {
        await using var site = await StartHealthAsync();

        var response = await PutJsonAsync(site, "/api/health/signals", Publication(Signal()), apiKey: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await Store(site).GetAsync("production"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("secre")]
    [InlineData("SECRET")]
    [InlineData(" secret")]
    public async Task Publish_WrongKey_IsUnauthorized(string key)
    {
        await using var site = await StartHealthAsync();

        var response = await PutJsonAsync(site, "/api/health/signals", Publication(Signal()), apiKey: key);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Publish_NoKeyConfiguredOnTheSite_IsServiceUnavailable(string? configured)
    {
        await using var site = await StartHealthAsync(configured);

        var response = await PutJsonAsync(site, "/api/health/signals", Publication(Signal()), apiKey: "anything");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Publish_GetIsNotAllowed()
    {
        await using var site = await StartHealthAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health/signals");
        request.Headers.Add("X-Api-Key", Key);

        var response = await site.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // ---- Health: publication shape ----

    [Theory]
    [InlineData("../prod")]
    [InlineData("")]
    [InlineData("prod name")]
    [InlineData("prod.example")]
    public async Task Publish_InvalidEnvironment_IsBadRequest(string environment)
    {
        await using var site = await StartHealthAsync();

        var response = await PutJsonAsync(site, "/api/health/signals", Publication(Signal(), environment));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Publish_NoSignals_IsBadRequest()
    {
        await using var site = await StartHealthAsync();

        var response = await PutJsonAsync(site, "/api/health/signals", Publication(string.Empty));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Publish_MoreThanTheLimitOfSignals_IsBadRequest()
    {
        await using var site = await StartHealthAsync();
        var many = string.Join(",", Enumerable.Range(0, 101).Select(i => Signal("s" + i)));

        var response = await PutJsonAsync(site, "/api/health/signals", Publication(many));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("has space")]
    [InlineData("")]
    [InlineData("dot.ted")]
    public async Task Publish_SignalIdWithSeparatorsOrBlank_IsBadRequest(string id)
    {
        await using var site = await StartHealthAsync();

        var response = await PutJsonAsync(site, "/api/health/signals", Publication(Signal(id)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Publish_BlankTitle_IsBadRequest()
    {
        await using var site = await StartHealthAsync();
        var blank = Signal().Replace("\"Quiz checks\"", "\"   \"");

        var response = await PutJsonAsync(site, "/api/health/signals", Publication(blank));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Publish_UnknownStatusName_IsBadRequest()
    {
        await using var site = await StartHealthAsync();
        var odd = Signal().Replace("\"Green\"", "\"Purple\"");

        var response = await PutJsonAsync(site, "/api/health/signals", Publication(odd));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Publish_NumericStatusOutsideTheEnum_IsBadRequest()
    {
        await using var site = await StartHealthAsync();
        var odd = Signal().Replace("\"Green\"", "7");

        var response = await PutJsonAsync(site, "/api/health/signals", Publication(odd));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Publish_MalformedJson_IsBadRequest()
    {
        await using var site = await StartHealthAsync();

        var response = await PutJsonAsync(site, "/api/health/signals", "{\"environment\":\"production\",\"signals\":[");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Publish_OversizedText_IsCutToItsLimits()
    {
        await using var site = await StartHealthAsync();
        var big = Signal(extra: ",\"detail\":\"" + Big(5000) + "\"").Replace("142 known good", Big(5000));

        var response = await PutJsonAsync(site, "/api/health/signals", Publication(big));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = (await Store(site).GetAsync("production")).Single();
        Assert.Equal(200, stored.Headline.Length);
        Assert.Equal(600, stored.Detail!.Length);
    }

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("https://evil.example")]
    [InlineData("javascript:alert(1)")]
    public async Task Publish_LinkThatLeavesTheSite_IsDropped(string link)
    {
        await using var site = await StartHealthAsync();

        await PutJsonAsync(site, "/api/health/signals", Publication(Signal(extra: ",\"link\":\"" + link + "\"")));

        Assert.Null((await Store(site).GetAsync("production")).Single().Link);
    }

    [Fact]
    public async Task Publish_LinkOnThisSite_IsKept()
    {
        await using var site = await StartHealthAsync();

        await PutJsonAsync(site, "/api/health/signals", Publication(Signal(extra: ",\"link\":\"/admin/quiz-checks\"")));

        Assert.Equal("/admin/quiz-checks", (await Store(site).GetAsync("production")).Single().Link);
    }

    [Fact]
    public async Task Publish_NormalAdminHealthLink_KeepsItsLink()
    {
        // The local-path rule must not drop an ordinary link: a valid publication with "/admin/health" keeps it.
        await using var site = await StartHealthAsync();

        var response = await PutJsonAsync(site, "/api/health/signals", Publication(Signal(extra: ",\"link\":\"/admin/health\"")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/admin/health", (await Store(site).GetAsync("production")).Single().Link);
    }

    [Fact]
    public async Task Publish_BackslashLinkThatBrowsersReadAsOtherHost_IsDropped()
    {
        // "/\evil.example" is read by browsers as "//evil.example": a link that starts with a slash and a backslash must not survive.
        await using var site = await StartHealthAsync();

        await PutJsonAsync(site, "/api/health/signals", Publication(Signal(extra: ",\"link\":\"/\\\\evil.example\"")));

        Assert.Null((await Store(site).GetAsync("production")).Single().Link);
    }

    [Fact]
    public async Task Publish_AsOfInTheFuture_IsClampedToNow()
    {
        await using var site = await StartHealthAsync();
        var future = Signal().Replace(Now, "2999-01-01T00:00:00Z");

        await PutJsonAsync(site, "/api/health/signals", Publication(future));

        Assert.True((await Store(site).GetAsync("production")).Single().AsOf <= DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task Publish_HugeExpectedHours_IsAcceptedAndJudgedWithoutAnError()
    {
        await using var site = await StartHealthAsync();
        var huge = Signal(extra: ",\"expectedEveryHours\":1e300");

        var response = await PutJsonAsync(site, "/api/health/signals", Publication(huge));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = (await Store(site).GetAsync("production")).Single();
        Assert.False(stored.IsStale(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Publish_InfinityExpectedHours_IsRefusedWith400()
    {
        // An expectation that overflows to infinity must be a clean refusal, not a failure to store the publication.
        await using var site = await StartHealthAsync();

        var response = await PutJsonAsync(site, "/api/health/signals", Publication(Signal(extra: ",\"expectedEveryHours\":1e400")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Publish_DuplicateIdsInOnePublication_KeepTheLast_AsRecorded()
    {
        await using var site = await StartHealthAsync();
        var first = Signal().Replace("142 known good", "first");
        var second = Signal().Replace("142 known good", "second");

        var response = await PutJsonAsync(site, "/api/health/signals", Publication(first + "," + second));

        Assert.Contains("\"accepted\":2", await response.Content.ReadAsStringAsync());
        Assert.Equal("second", (await Store(site).GetAsync("production")).Single().Headline);
    }

    [Fact]
    public async Task Publish_ThousandCharacterSignalId_IsBadRequest()
    {
        // A signal id is a dictionary key stored with every publication; nothing in this door caps its length.
        await using var site = await StartHealthAsync();

        var response = await PutJsonAsync(site, "/api/health/signals", Publication(Signal(Big(1000))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Publish_ExtremeValue_IsStoredAsSent()
    {
        await using var site = await StartHealthAsync();

        var response = await PutJsonAsync(site, "/api/health/signals", Publication(Signal(extra: ",\"value\":-1e308,\"unit\":\"" + Big(40) + "\"")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = (await Store(site).GetAsync("production")).Single();
        Assert.Equal(12, stored.Unit!.Length);
    }

    // ---- Health: hosted reports ----

    [Fact]
    public async Task Report_ValidHtml_IsStored()
    {
        await using var site = await StartHealthAsync();

        var response = await PutBodyAsync(site, "/api/health/reports/production/quiz-checks", new StringContent("<h1>ok</h1>", Encoding.UTF8, "text/html"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var reports = site.Services.GetRequiredService<HealthReportStore>();
        Assert.Equal("<h1>ok</h1>", await reports.GetAsync("production", "quiz-checks"));
    }

    [Fact]
    public async Task Report_AtTheLimit_IsStored()
    {
        await using var site = await StartHealthAsync();
        var atLimit = new ByteArrayContent(new byte[HealthReportStore.MaxBytes]);

        var response = await PutBodyAsync(site, "/api/health/reports/production/big", atLimit);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Report_OverTheLimitWithALength_IsPayloadTooLarge()
    {
        await using var site = await StartHealthAsync();
        var over = new ByteArrayContent(new byte[HealthReportStore.MaxBytes + 1]);

        var response = await PutBodyAsync(site, "/api/health/reports/production/big", over);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact(Skip = "FINDING F-W1 (Medium): a chunked report over 2 MB is read whole before the size check, then SaveAsync throws (500, not 413); desired: 413 without reading it all.")]
    public async Task Report_OverTheLimitWithoutALength_IsPayloadTooLarge()
    {
        // A chunked upload has no Content-Length, so the early refusal does not see it; the size must still be enforced.
        await using var site = await StartHealthAsync();
        var chunked = new StreamContent(new UnsizedStream(new byte[HealthReportStore.MaxBytes + 1]));

        var response = await PutBodyAsync(site, "/api/health/reports/production/big", chunked);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    public async Task Report_EmptyBody_IsBadRequest(string body)
    {
        await using var site = await StartHealthAsync();

        var response = await PutBodyAsync(site, "/api/health/reports/production/quiz-checks", new StringContent(body, Encoding.UTF8, "text/html"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("dot.ted")]
    [InlineData("bad id")]
    public async Task Report_IdWithSeparators_IsBadRequest(string id)
    {
        await using var site = await StartHealthAsync();

        var response = await PutBodyAsync(site, "/api/health/reports/production/" + Uri.EscapeDataString(id), new StringContent("<p>x</p>"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Report_InvalidEnvironment_IsBadRequest()
    {
        await using var site = await StartHealthAsync();

        var response = await PutBodyAsync(site, "/api/health/reports/..prod/quiz-checks", new StringContent("<p>x</p>"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Report_WithoutTheKey_IsUnauthorized()
    {
        await using var site = await StartHealthAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/health/reports/production/quiz-checks") { Content = new StringContent("<p>x</p>") };

        var response = await site.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Audit: the actor a request is recorded under ----

    [Fact]
    public async Task Audit_NullStore_IsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddAuditTrail(null!));
    }

    [Fact]
    public void Audit_NoUser_OnAPage_IsTheSystemWithItsPath()
    {
        var actor = AuditActors.From(null, "/quiz/7", "trace");

        Assert.Equal(AuditActor.System.Id, actor.Id);
        Assert.Equal("/quiz/7", actor.Path);
    }

    [Theory]
    [InlineData("/api/quiz")]
    [InlineData("/API/quiz")]
    public void Audit_NoUser_OnTheApi_IsTheApi(string path)
    {
        Assert.Equal("api", AuditActors.From(null, path).Id);
    }

    [Fact]
    public void Audit_PathThatOnlyLooksLikeTheApi_IsNotTheApi()
    {
        Assert.Equal(AuditActor.System.Id, AuditActors.From(null, "/apix/quiz").Id);
    }

    [Fact]
    public void Audit_SignedInUser_IsTheWebActor_EvenOnTheApi()
    {
        var user = Principal("u1", "sam@example.com", "test");

        var actor = AuditActors.From(user, "/api/quiz");

        Assert.Equal("u1", actor.Id);
        Assert.Equal("web", actor.Source);
    }

    [Fact]
    public void Audit_UnauthenticatedPrincipalWithAnId_IsNotTheUser()
    {
        var user = Principal("u1", "sam@example.com", authenticationType: null);

        Assert.Equal(AuditActor.System.Id, AuditActors.From(user, "/quiz").Id);
    }

    [Fact(Skip = "FINDING F-W2 (Medium): while an admin masquerades, the audit actor is the viewed account (actual id u2), not the admin who made the change (u1); desired: u1.")]
    public void Audit_MasqueradingAdmin_IsRecordedAsTheAdmin()
    {
        // The trail is for "who changed this". While an admin masquerades, the entry names the account being viewed and not the admin who made the change.
        var claims = new AccountClaims("probe:");
        var target = new User { Id = "u2", Email = "member@example.com", DisplayName = "Member" };
        var principal = claims.CreateMasqueradePrincipal(target, "Member", "u1", "test");

        var actor = AuditActors.From(principal, "/admin/users");

        Assert.Equal("u1", actor.Id);
    }

    [Fact]
    public async Task Audit_RequestWithNoUser_IsRecordedAsTheSystem()
    {
        await using var site = await EndpointSite.StartPlainAsync(EndpointSite.NewFolder(), builder => builder.Services.AddAuditTrail(new FileDocumentStore(Path.Combine(EndpointSite.NewFolder(), "audit"))), app =>
        {
            app.UseAuditActors();
            app.MapGet("/quiz/{id}", (IAuditActorContext actors) => Results.Text(actors.Current.Id));
        });

        var response = await site.GetAsync("/quiz/7");

        Assert.Equal(AuditActor.System.Id, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Audit_RequestToTheApi_IsRecordedAsTheApi()
    {
        await using var site = await EndpointSite.StartPlainAsync(EndpointSite.NewFolder(), builder => builder.Services.AddAuditTrail(new FileDocumentStore(Path.Combine(EndpointSite.NewFolder(), "audit"))), app =>
        {
            app.UseAuditActors();
            app.MapGet("/api/probe", (IAuditActorContext actors) => Results.Text(actors.Current.Id));
        });

        var response = await site.GetAsync("/api/probe");

        Assert.Equal("api", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Audit_ActorIsNotLeftBehindAfterTheRequest()
    {
        await using var site = await EndpointSite.StartPlainAsync(EndpointSite.NewFolder(), builder => builder.Services.AddAuditTrail(new FileDocumentStore(Path.Combine(EndpointSite.NewFolder(), "audit"))), app =>
        {
            app.UseAuditActors();
            app.MapGet("/api/probe", () => Results.Text("ok"));
        });
        await site.GetAsync("/api/probe");
        var context = site.Services.GetRequiredService<IAuditActorContext>();

        Assert.Equal(AuditActor.System.Id, context.Current.Id);
    }

    private static ClaimsPrincipal Principal(string id, string email, string? authenticationType)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Name, email)],
            authenticationType);
        return new ClaimsPrincipal(identity);
    }

    // ---- The form gate ----

    [Fact]
    public async Task FormGate_JsonBody_IsBadRequestBeforeTheHandler()
    {
        await using var site = await EndpointSite.StartPlainAsync(EndpointSite.NewFolder(), _ => { }, app => app.MapFormGroup().MapPost("/probe/form", () => Results.Text("ran")), antiforgery: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/probe/form") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

        var response = await site.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FormGate_FormWithoutAToken_IsBadRequestBeforeTheHandler()
    {
        await using var site = await EndpointSite.StartPlainAsync(EndpointSite.NewFolder(), _ => { }, app => app.MapFormGroup().MapPost("/probe/form", () => Results.Text("ran")), antiforgery: true);

        var response = await site.PostFormAsync("/probe/form", [], withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual("ran", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FormGate_FormWithAToken_RunsTheHandler()
    {
        await using var site = await EndpointSite.StartPlainAsync(EndpointSite.NewFolder(), _ => { }, app => app.MapFormGroup().MapPost("/probe/form", () => Results.Text("ran")), antiforgery: true);

        var response = await site.PostFormAsync("/probe/form", []);

        Assert.Equal("ran", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FormGate_GetIsNotAllowed()
    {
        await using var site = await EndpointSite.StartPlainAsync(EndpointSite.NewFolder(), _ => { }, app => app.MapFormGroup().MapPost("/probe/form", () => Results.Text("ran")), antiforgery: true);

        var response = await site.GetAsync("/probe/form");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task FormGate_WithoutTheAntiforgeryServiceRegistered_FailsLoudly_AsRecorded()
    {
        // A host that maps the form endpoints without AddAntiforgery gets an exception on every form post, not a refusal: the app fails loudly, it does not open.
        await using var site = await EndpointSite.StartPlainAsync(EndpointSite.NewFolder(), _ => { }, app => app.MapFormGroup().MapPost("/probe/form", () => Results.Text("ran")), antiforgery: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => site.PostFormAsync("/probe/form", [], withToken: false));
    }
}
