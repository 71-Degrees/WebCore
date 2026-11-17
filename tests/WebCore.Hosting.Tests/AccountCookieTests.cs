/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebCore.Accounts;
using WebCore.Hosting.Accounts;
using WebCore.Storage;

namespace WebCore.Hosting.Tests;

public sealed class AccountCookieTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WebCoreCookie_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TheCookieHandler_PointsAtNoRealLogoutPath_SoItNeverSwallowsTheLogoutEndpointsRedirect()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.AddWebCoreAccounts(o =>
        {
            o.BrandName = "Test";
            o.Roles = new AccountRoles(["Member", "Admin"], "Member", "Admin");
            o.UsersStore = new FileDocumentStore(Path.Combine(_folder, "users"));
            o.ProfilesStore = new FileDocumentStore(Path.Combine(_folder, "profiles"));
        });
        await using var app = builder.Build();

        var cookie = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);

        Assert.False(cookie.LogoutPath.Equals(new PathString("/account/logout")));
    }
}
