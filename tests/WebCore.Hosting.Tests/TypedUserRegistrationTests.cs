/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using WebCore.Accounts;
using WebCore.Hosting.Accounts;
using WebCore.Storage;

namespace WebCore.Hosting.Tests;

public sealed class TypedUserRegistrationTests : IDisposable
{
    /// <summary>An app's own account class, as in <see cref="TypedUserTests"/> (WebCore.Tests): one field of its own. Public on purpose: the counting proxy
    /// (<see cref="StorageCalls"/>) is generated in its own assembly, which cannot see a private nested type.</summary>
    public sealed class AppUser : User
    {
        public string? SignupIp { get; set; }
    }

    private static readonly AccountRoles Roles = new(["Member", "Admin"], "Member", "Admin");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WebCoreTypedRegistration_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.AddWebCoreAccounts<UserProfile, AppUser>(o =>
        {
            o.BrandName = "CrateDigger";
            o.Roles = Roles;
            o.UsersStore = new FileDocumentStore(Path.Combine(_folder, "users"));
            o.ProfilesStore = new FileDocumentStore(Path.Combine(_folder, "profiles"));
            o.CookieName = "typed_auth";
            o.ClaimPrefix = "typed:";
        });
        return builder.Build();
    }

    [Fact]
    public async Task AppUserClass_ResolvesBothUserInterfaces_ToOneCachedRepository()
    {
        await using var app = BuildApp();

        var plain = app.Services.GetRequiredService<IUserRepository>();
        var typed = app.Services.GetRequiredService<IUserRepository<AppUser>>();

        Assert.Same((object)plain, typed);
    }

    [Fact]
    public async Task APlainCreate_ThroughTheRegisteredServices_IsReadBackAsTheAppsClass()
    {
        await using var app = BuildApp();
        var created = await app.Services.GetRequiredService<IUserRepository>().CreateAsync(new User { Email = "f@example.com" });

        var read = await app.Services.GetRequiredService<IUserRepository<AppUser>>().GetByIdAsync(created.Id);

        Assert.IsType<AppUser>(read);
        Assert.Null(read!.SignupIp);
    }
}
