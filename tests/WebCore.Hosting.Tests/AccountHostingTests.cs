/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
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

    [Fact]
    public void TheEmailFingerprint_IsShortAndIgnoresCaseAndSpace() =>
        Assert.Equal(SignInDiagnostics.EmailFingerprint(" Sam@Example.com "), SignInDiagnostics.EmailFingerprint("sam@example.com"));
}
