/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using WebCore.Accounts;
using WebCore.Email;
using WebCore.Hosting.Accounts;
using WebCore.Storage;

namespace WebCore.Hosting.Tests;

public sealed class AccountHostingTests : IDisposable
{
    private static readonly AccountRoles Roles = new(["Member", "Admin"], "Member", "Admin");
    private static readonly AccountClaims Claims = new("cratedigger:");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WebCoreAccountHosting_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static System.Security.Claims.ClaimsPrincipal Cookie(string role, string? stamp = null) =>
        Claims.CreatePrincipal(new User { Id = "u1", Email = "a@example.com", DisplayName = "A", SecurityStamp = stamp }, role, "test");

    [Fact]
    public void ACookieMatchingTheStoredAccount_IsKept() =>
        Assert.Equal(SessionVerdict.Keep, AccountSessionEvents.Judge(Cookie("Member"), new UserProfile { Role = "Member" }, null, Claims));

    [Fact]
    public void AChangedRole_RefreshesTheCookie() =>
        Assert.Equal(SessionVerdict.RefreshRole, AccountSessionEvents.Judge(Cookie("Member"), new UserProfile { Role = "Admin" }, null, Claims));

    [Fact]
    public void AMissingOrDisabledProfile_IsRejected()
    {
        Assert.Equal(SessionVerdict.Reject, AccountSessionEvents.Judge(Cookie("Member"), null, null, Claims));
        Assert.Equal(SessionVerdict.Reject, AccountSessionEvents.Judge(Cookie("Member"), new UserProfile { Role = "Member", IsDisabled = true }, null, Claims));
    }

    [Fact]
    public void APasswordChangedSinceTheCookie_RejectsIt()
    {
        // A cookie from before the account had a stamp, after its first password change.
        Assert.Equal(SessionVerdict.Reject, AccountSessionEvents.Judge(Cookie("Member"), new UserProfile { Role = "Member" }, "new", Claims));

        // A cookie with an older stamp.
        Assert.Equal(SessionVerdict.Reject, AccountSessionEvents.Judge(Cookie("Member", "old"), new UserProfile { Role = "Member" }, "new", Claims));
        Assert.Equal(SessionVerdict.Keep, AccountSessionEvents.Judge(Cookie("Member", "new"), new UserProfile { Role = "Member" }, "new", Claims));
    }

    [Fact]
    public async Task AddWebCoreAccounts_WiresTheCookieThePolicyAndTheServices()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration["Auth:AdminEmails"] = "Owner@Example.com, other@example.com";
        builder.AddWebCoreAccounts(o =>
        {
            o.BrandName = "CrateDigger";
            o.Roles = Roles;
            o.UsersStore = new FileDocumentStore(Path.Combine(_folder, "users"));
            o.ProfilesStore = new FileDocumentStore(Path.Combine(_folder, "profiles"));
            o.CookieName = "cratedigger_auth";
            o.ClaimPrefix = "cratedigger:";
        });
        await using var app = builder.Build();
        var services = app.Services;

        var cookie = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        Assert.Equal("cratedigger_auth", cookie.Cookie.Name);
        Assert.Equal("/account/login", cookie.LoginPath);
        Assert.Equal("cratedigger:email_confirmed", services.GetRequiredService<AccountClaims>().EmailConfirmed);
        Assert.IsType<NullEmailSender>(services.GetRequiredService<IEmailSender>());
        Assert.False(services.GetRequiredService<AccountEmailOptions>().DeliveryConfigured);

        var policy = await services.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(AccountPolicies.Admin);
        var requirement = Assert.Single(policy!.Requirements.OfType<Microsoft.AspNetCore.Authorization.Infrastructure.RolesAuthorizationRequirement>());
        Assert.Equal(["Admin"], requirement.AllowedRoles);
        Assert.Equal(["other@example.com", "owner@example.com"], AccountRegistration.AdminEmails(builder.Configuration).Order());
    }

    [Fact]
    public void AddWebCoreAccounts_RefusesOptionsWithoutRolesOrStores() =>
        Assert.Throws<InvalidOperationException>(() => WebApplication.CreateBuilder().AddWebCoreAccounts(o => o.BrandName = "X"));

    private sealed class RefuseEveryone : IAccountSignupPolicy
    {
        public string? Refusal(HttpContext http, string email, string displayName) => "no";

        public Task AccountCreatedAsync(HttpContext http, User user, IFormCollection form) => Task.CompletedTask;
    }

    private static AccountFormMemory FormMemory(bool keepEmailsOutOfUrls) =>
        new(new EphemeralDataProtectionProvider(), new AccountOptions { KeepEmailsOutOfUrls = keepEmailsOutOfUrls }, NullLogger<AccountFormMemory>.Instance);

    [Fact]
    public void ByDefault_TheEmailStaysInTheQueryString_AsCrateDiggerHasIt()
    {
        var http = new DefaultHttpContext();

        Assert.Equal("&email=sam%40example.com", FormMemory(keepEmailsOutOfUrls: false).QueryPart(http, "sam@example.com"));
        Assert.False(http.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public void KeptOutOfUrls_TheEmailTravelsInASealedCookie_AndReadsBack()
    {
        var memory = FormMemory(keepEmailsOutOfUrls: true);
        var redirect = new DefaultHttpContext();

        Assert.Equal(string.Empty, memory.QueryPart(redirect, "sam@example.com"));
        var setCookie = redirect.Response.Headers.SetCookie.ToString();
        Assert.DoesNotContain("example.com", setCookie, StringComparison.OrdinalIgnoreCase);

        var next = new DefaultHttpContext();
        next.Request.Headers.Cookie = setCookie.Split(';')[0];
        Assert.Equal("sam@example.com", memory.Recall(next));
    }

    [Fact]
    public void AnAlteredFormCookie_ReadsAsNothing()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = "webcore_form_email=not-a-sealed-value";

        Assert.Null(FormMemory(keepEmailsOutOfUrls: true).Recall(http));
    }

    [Fact]
    public async Task TheSignupPolicy_DefaultsToAllowingEveryone_AndAnAppsOwnIsKept()
    {
        await using var plain = BuildApp(_ => { });
        Assert.IsType<AllowAllSignups>(plain.Services.GetRequiredService<IAccountSignupPolicy>());

        await using var screened = BuildApp(services => services.AddSingleton<IAccountSignupPolicy, RefuseEveryone>());
        Assert.IsType<RefuseEveryone>(screened.Services.GetRequiredService<IAccountSignupPolicy>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASignIn_IsPersistentOnlyWhenTheAppAsksForIt(bool persistent)
    {
        await using var app = BuildApp(_ => { }, persistent);
        var capture = new CapturingAuthentication();
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton<IAuthenticationService>(capture).BuildServiceProvider() };

        await app.Services.GetRequiredService<AccountSignIn>().SignInAsync(http, new User { Email = "a@example.com", DisplayName = "A" });

        Assert.Equal(persistent, capture.Properties!.IsPersistent);
        Assert.Equal("Member", AccountClaims.RoleOf(capture.Principal!));
    }

    private WebApplication BuildApp(Action<IServiceCollection> before, bool persistent = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        before(builder.Services);
        builder.AddWebCoreAccounts(o =>
        {
            o.BrandName = "Test";
            o.Roles = Roles;
            o.UsersStore = new FileDocumentStore(Path.Combine(_folder, "users"));
            o.ProfilesStore = new FileDocumentStore(Path.Combine(_folder, "profiles"));
            o.PersistentSignIn = persistent;
        });
        return builder.Build();
    }

    private sealed class CapturingAuthentication : IAuthenticationService
    {
        public System.Security.Claims.ClaimsPrincipal? Principal { get; private set; }

        public AuthenticationProperties? Properties { get; private set; }

        public Task SignInAsync(HttpContext context, string? scheme, System.Security.Claims.ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            Principal = principal;
            Properties = properties;
            return Task.CompletedTask;
        }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    [Fact]
    public void PasswordGuessesAtOneAccount_AreCapped_FromAnyAddress()
    {
        var limits = new AccountRateLimits();
        static HttpContext From(int n) => new DefaultHttpContext { Connection = { RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0." + n) } };

        for (var i = 1; i <= 10; i++)
        {
            Assert.True(limits.TryAcquirePassword(From(i), "sam@example.com"));
        }

        Assert.False(limits.TryAcquirePassword(From(11), " Sam@Example.com "));
        Assert.True(limits.TryAcquirePassword(From(11), "other@example.com"));
    }

    [Fact]
    public void TheEmailFingerprint_IsShortAndIgnoresCaseAndSpace() =>
        Assert.Equal(SignInDiagnostics.EmailFingerprint(" Sam@Example.com "), SignInDiagnostics.EmailFingerprint("sam@example.com"));
}
