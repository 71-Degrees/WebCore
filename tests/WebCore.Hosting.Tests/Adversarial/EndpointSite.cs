/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WebCore.Accounts;
using WebCore.Email;
using WebCore.Hosting.Accounts;
using WebCore.Storage;

namespace WebCore.Hosting.Tests.Adversarial;

/// <summary>Every mail the site would send, kept in memory. Codes are read back from the body the way a person reads them.</summary>
internal sealed class RecordingEmailSender : IEmailSender
{
    private static readonly System.Text.RegularExpressions.Regex CodePattern = new("<strong>(\\d{6})</strong>");

    public List<(string To, string Subject, string Body)> Sent { get; } = [];

    public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        Sent.Add((toEmail, subject, htmlBody));
        return Task.CompletedTask;
    }

    /// <summary>The six-digit code of the latest mail to <paramref name="email"/>, or null when none was sent.</summary>
    public string? CodeFor(string email)
    {
        var mail = Sent.LastOrDefault(m => m.To == email);
        var match = mail.Body is null ? null : CodePattern.Match(mail.Body);
        return match is { Success: true } ? match.Groups[1].Value : null;
    }
}

/// <summary>Keeps the cookies the server sets, so a test can sign in and then send the next form the way a browser would. A deleted cookie is removed.</summary>
internal sealed class CookieJar(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Cookies => _cookies;

    public void Forget(string name) => _cookies.Remove(name);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => c.Key + "=" + c.Value)));
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            foreach (var raw in values)
            {
                Store(raw);
            }
        }

        return response;
    }

    private void Store(string raw)
    {
        var pair = raw.Split(';')[0];
        var equals = pair.IndexOf('=');
        if (equals <= 0)
        {
            return;
        }

        var name = pair[..equals];
        var value = pair[(equals + 1)..];
        var deleted = value.Length == 0
            || raw.Contains("max-age=0", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);
        if (deleted)
        {
            _cookies.Remove(name);
        }
        else
        {
            _cookies[name] = value;
        }
    }
}

/// <summary>
/// An in-memory site for the endpoint probes: a TestServer, temp-folder stores, a recording mail sender, and the account
/// module with its real endpoints. The probe routes (<c>/probe/who</c>, <c>/probe/antiforgery</c>) exist only in the test host.
/// Dispose deletes the temp folder.
/// </summary>
internal sealed class EndpointSite : IAsyncDisposable
{
    public const string Password = "Correct horse 9";
    public const string Brand = "Probe";
    public const string SignInCookie = "webcore_auth";
    public const string CodeClientCookie = "webcore_code_client";

    private static readonly AccountRoles Roles = new(["Member", "Admin"], "Member", "Admin");

    private readonly string _folder;
    private readonly WebApplication _app;
    private readonly CookieJar _jar;
    private readonly HttpClient _client;
    private readonly HttpClient _anonymous;

    private EndpointSite(string folder, WebApplication app, RecordingEmailSender mail)
    {
        _folder = folder;
        _app = app;
        Mail = mail;
        var server = app.Services.GetRequiredService<IServer>() as TestServer ?? throw new InvalidOperationException("The probe site is not on a TestServer.");
        var handler = server.CreateHandler();
        _jar = new CookieJar(handler);
        _client = new HttpClient(_jar, disposeHandler: true) { BaseAddress = new Uri("http://probe.test/") };
        _anonymous = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://probe.test/") };
    }

    public RecordingEmailSender Mail { get; }

    public IServiceProvider Services => _app.Services;

    public IReadOnlyDictionary<string, string> Cookies => _jar.Cookies;

    public static string NewFolder() => Path.Combine(Path.GetTempPath(), "WebCoreEndpointProbe_" + Guid.NewGuid().ToString("N"));

    /// <summary>A string of <paramref name="length"/> plain characters, for the oversized-field probes.</summary>
    public static string Big(int length) => new('x', length);

    /// <summary>The account module with its real endpoints. <paramref name="configure"/> may change the options; <paramref name="map"/> adds routes.</summary>
    public static async Task<EndpointSite> StartAccountsAsync(string folder, Action<AccountOptions>? configure = null, Action<WebApplication>? map = null)
    {
        var mail = new RecordingEmailSender();
        return await StartCoreAsync(folder, mail, builder =>
        {
            builder.Services.AddSingleton<IEmailSender>(mail);
            builder.AddWebCoreAccounts(o =>
            {
                o.BrandName = Brand;
                o.Roles = Roles;
                o.UsersStore = new FileDocumentStore(Path.Combine(folder, "users"));
                o.ProfilesStore = new FileDocumentStore(Path.Combine(folder, "profiles"));
                o.EmailDeliveryConfigured = true;
                configure?.Invoke(o);
            });
        }, app =>
        {
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGet("/probe/who", (HttpContext http, AccountClaims claims) => Results.Text(Describe(http.User, claims)));
            app.MapWebCoreAccountEndpoints();
            map?.Invoke(app);
        }, antiforgery: true);
    }

    /// <summary>A site with no account module: the caller registers its own services in <paramref name="prepare"/> and maps its routes in <paramref name="map"/>.
    /// With <paramref name="antiforgery"/> the antiforgery service is registered and <c>/probe/antiforgery</c> hands out a token.</summary>
    public static Task<EndpointSite> StartPlainAsync(string folder, Action<WebApplicationBuilder> prepare, Action<WebApplication> map, bool antiforgery = false) =>
        StartCoreAsync(folder, new RecordingEmailSender(), prepare, map, antiforgery);

    private static async Task<EndpointSite> StartCoreAsync(string folder, RecordingEmailSender mail, Action<WebApplicationBuilder> prepare, Action<WebApplication> map, bool antiforgery)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        if (antiforgery)
        {
            builder.Services.AddAntiforgery();
        }

        prepare(builder);
        var app = builder.Build();
        if (antiforgery)
        {
            app.MapGet("/probe/antiforgery", (HttpContext http, IAntiforgery tokens) => tokens.GetAndStoreTokens(http).RequestToken);
        }

        map(app);
        await app.StartAsync();
        return new EndpointSite(folder, app, mail);
    }

    private static string Describe(ClaimsPrincipal user, AccountClaims claims)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return "anonymous";
        }

        return $"id={AccountClaims.UserIdOf(user)};role={AccountClaims.RoleOf(user)};masquerade={claims.IsMasquerading(user)}";
    }

    /// <summary>Key/value pairs for a form, duplicates allowed.</summary>
    public static KeyValuePair<string, string>[] Fields(params (string Key, string Value)[] pairs) =>
        pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)).ToArray();

    /// <summary>The Location header as the server wrote it (a relative redirect keeps its escaping).</summary>
    public static string LocationOf(HttpResponseMessage response) => response.Headers.Location?.OriginalString ?? string.Empty;

    public Task<HttpResponseMessage> GetAsync(string path) => _client.GetAsync(path);

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => _client.SendAsync(request);

    /// <summary>The antiforgery token for this browser (it also sets the antiforgery cookie the form needs).</summary>
    public async Task<string> TokenAsync() => await (await _client.GetAsync("/probe/antiforgery")).Content.ReadAsStringAsync();

    /// <summary>A form post from this browser. With <paramref name="withToken"/>, a fresh token is added as the first field.</summary>
    public async Task<HttpResponseMessage> PostFormAsync(string path, KeyValuePair<string, string>[] fields, bool withToken = true)
    {
        var list = fields.ToList();
        if (withToken)
        {
            list.Insert(0, new KeyValuePair<string, string>("__RequestVerificationToken", await TokenAsync()));
        }

        return await _client.PostAsync(path, new FormUrlEncodedContent(list));
    }

    /// <summary>A form post from a browser with no cookies at all (for a token used somewhere else).</summary>
    public Task<HttpResponseMessage> PostFormWithoutCookiesAsync(string path, KeyValuePair<string, string>[] fields) =>
        _anonymous.PostAsync(path, new FormUrlEncodedContent(fields));

    /// <summary>Any request from a browser with no cookies, with a body of the caller's choosing (for JSON and other content types).</summary>
    public Task<HttpResponseMessage> SendAnonymousAsync(HttpRequestMessage request) => _anonymous.SendAsync(request);

    /// <summary>A request from this browser with one cookie of the caller's choosing, replacing nothing in the jar.</summary>
    public async Task<HttpResponseMessage> SendWithCookieAsync(HttpMethod method, string path, string name, string value)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", name + "=" + value);
        return await _anonymous.SendAsync(request);
    }

    public async Task<string> WhoAsync() => await (await _client.GetAsync("/probe/who")).Content.ReadAsStringAsync();

    public void ForgetCookie(string name) => _jar.Forget(name);

    public string? CookieValue(string name) => _jar.Cookies.TryGetValue(name, out var value) ? value : null;

    public Task<HttpResponseMessage> SignInAsync(string email, string password, string returnUrl = "/") =>
        PostFormAsync("/account/login-submit", Fields(("email", email), ("password", password), ("returnUrl", returnUrl)));

    /// <summary>Creates an account straight in the store (no endpoint), with the password hashed as the site does, and the role and disabled flag set.</summary>
    public async Task<User> CreateAccountAsync(string email, string? password, string? role = null, bool disabled = false, bool verified = true)
    {
        var users = Services.GetRequiredService<IUserRepository>();
        var profiles = Services.GetRequiredService<IUserProfileRepository>();
        var user = await users.CreateAsync(new User
        {
            Email = email,
            DisplayName = email.Split('@')[0],
            PasswordHash = password is null ? string.Empty : PasswordHasher.Hash(password),
            EmailVerifiedAt = verified ? DateTimeOffset.UtcNow : null,
            SecurityStamp = Guid.NewGuid().ToString("N"),
        });
        // The profile is made the way the app makes it on first sign-in; GetAsync alone never creates one, so a role or disable change would find nothing to change.
        await profiles.EnsureProfileAsync(user.Id, user.DisplayName);
        if (role is not null)
        {
            await profiles.SetRoleAsync(user.Id, role);
        }

        if (disabled)
        {
            await profiles.SetDisabledAsync(user.Id, true);
        }

        return user;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _anonymous.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
