/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using WebCore.Accounts;
using WebCore.Email;
using WebCore.Hosting.Accounts;
using WebCore.Storage;

namespace WebCore.Hosting.Tests;

public sealed class AccountMailSenderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WebCoreMailSender_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class RecordingSender : IEmailSender
    {
        public int Count { get; private set; }

        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
        {
            Count++;
            return Task.CompletedTask;
        }
    }

    private WebApplication Build(Action<AccountOptions> extra)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.AddWebCoreAccounts(o =>
        {
            o.BrandName = "Test";
            o.Roles = new AccountRoles(["Member", "Admin"], "Member", "Admin");
            o.UsersStore = new FileDocumentStore(Path.Combine(_folder, "users"));
            o.ProfilesStore = new FileDocumentStore(Path.Combine(_folder, "profiles"));
            o.EmailDeliveryConfigured = true;
            extra(o);
        });
        return builder.Build();
    }

    [Fact]
    public async Task AccountMail_UsesTheAppsWrapper_WhenOneIsGiven()
    {
        var wrapper = new RecordingSender();
        await using var app = Build(o => o.AccountMailSender = _ => wrapper);

        await app.Services.GetRequiredService<AccountEmailService>().RequestSignInCodeAsync("a@example.com", "client-1");

        Assert.Equal(1, wrapper.Count);
    }

    [Fact]
    public async Task AccountMail_UsesTheRegisteredSender_ByDefault()
    {
        var registered = new RecordingSender();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Services.AddSingleton<IEmailSender>(registered);
        builder.AddWebCoreAccounts(o =>
        {
            o.BrandName = "Test";
            o.Roles = new AccountRoles(["Member", "Admin"], "Member", "Admin");
            o.UsersStore = new FileDocumentStore(Path.Combine(_folder, "users"));
            o.ProfilesStore = new FileDocumentStore(Path.Combine(_folder, "profiles"));
            o.EmailDeliveryConfigured = true;
        });
        await using var app = builder.Build();

        await app.Services.GetRequiredService<AccountEmailService>().RequestSignInCodeAsync("a@example.com", "client-1");

        Assert.Equal(1, registered.Count);
    }
}
