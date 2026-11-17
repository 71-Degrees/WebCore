/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebCore.Accounts;
using WebCore.Caching;
using WebCore.Email;

namespace WebCore.Hosting.Accounts;

/// <summary>Policy names for <c>[Authorize(Policy = ...)]</c> and <c>RequireAuthorization(...)</c>.</summary>
public static class AccountPolicies
{
    /// <summary>Requires the role claim to be the app's <see cref="AccountRoles.Admin"/>.</summary>
    public const string Admin = "Admin";
}

/// <summary>
/// Accounts for an app in one call: users and profiles (cached, see docs/accounts.md), emailed codes, passwords, the sign-in cookie with its checks, and
/// the Admin policy. The app registers its email sender first (or none), then calls this, then <see cref="AccountEndpoints.MapWebCoreAccountEndpoints"/>.
/// Reads the same configuration keys every app already sets: <c>Auth:AdminEmails</c> and <c>AccountEmail:*</c>.
/// </summary>
public static class AccountRegistration
{
    public static WebApplicationBuilder AddWebCoreAccounts(this WebApplicationBuilder builder, Action<AccountOptions> configure) =>
        builder.AddWebCoreAccounts<UserProfile>(configure);

    /// <summary>Accounts whose profiles are the app's own class (fields of its own, real defaults): <see cref="IUserProfileRepository{TProfile}"/> reads
    /// and updates them, and WebCore's own <see cref="IUserProfileRepository"/> is the same cached repository.</summary>
    public static WebApplicationBuilder AddWebCoreAccounts<TProfile>(this WebApplicationBuilder builder, Action<AccountOptions> configure)
        where TProfile : UserProfile, new() =>
        builder.AddWebCoreAccounts<TProfile, User>(configure);

    /// <summary>Accounts whose users and profiles are the app's own classes (fields of its own on either): <see cref="IUserRepository{TUser}"/> and
    /// <see cref="IUserProfileRepository{TProfile}"/> read and update them, and WebCore's plain interfaces are the same cached repositories.</summary>
    public static WebApplicationBuilder AddWebCoreAccounts<TProfile, TUser>(this WebApplicationBuilder builder, Action<AccountOptions> configure)
        where TProfile : UserProfile, new()
        where TUser : User, new()
    {
        var options = new AccountOptions();
        configure(options);
        options.Validate();

        var services = builder.Services;
        services.AddSingleton(options);
        services.AddSingleton(options.Roles!);
        services.AddSingleton(new AccountClaims(options.ClaimPrefix));
        AddRepositories<TProfile, TUser>(services, options);

        // Unconfigured email is safely off: a sender that only logs, never a startup crash for a missing secret.
        services.TryAddSingleton<IEmailSender, NullEmailSender>();
        var emailOptions = EmailOptions(builder, options);
        services.AddSingleton(emailOptions);
        services.AddSingleton(sp => new AccountEmailService(
            sp.GetRequiredService<IUserRepository>(),
            sp.GetRequiredService<IUserProfileRepository>(),
            MailSender(sp, options),
            options.Roles!,
            emailOptions,
            onSendFailure: ex => Log(sp, options).LogWarning(ex, "Could not send a code email")));
        services.AddSingleton(sp => new PasswordService(
            sp.GetRequiredService<IUserRepository>(),
            MailSender(sp, options),
            emailOptions,
            sp.GetService<TimeProvider>(),
            ex => Log(sp, options).LogWarning(ex, "A password side effect failed: a changed-password notice was not sent, or a password hash was not upgraded")));

        services.AddSingleton<AccountRateLimits>();
        services.AddSingleton<AccountSignIn>();
        services.AddSingleton<AccountFormMemory>();
        services.TryAddSingleton<IAccountSignupPolicy, AllowAllSignups>();
        services.AddSingleton<AccountServicesBundle>();
        services.AddSingleton(new AccountSessionEvents(options, AdminEmails(builder.Configuration)));
        AddCookie(services, options);
        services.AddAuthorizationBuilder().AddPolicy(AccountPolicies.Admin, policy => policy.RequireRole(options.Roles!.Admin));
        return builder;
    }

    // Both are wrapped in short-lived caches because the cookie check reads a profile and an account on every request; StorageCalls (under the cache)
    // counts what gets through to real storage for the read budgets.
    private static void AddRepositories<TProfile, TUser>(IServiceCollection services, AccountOptions options)
        where TProfile : UserProfile, new()
        where TUser : User, new()
    {
        services.AddSingleton(_ => new CachedUserRepository<TUser>(StorageCalls.Wrap<IUserRepository<TUser>>(new UserRepository<TUser>(options.UsersStore!))));
        services.AddSingleton<IUserRepository<TUser>>(sp => sp.GetRequiredService<CachedUserRepository<TUser>>());
        services.AddSingleton<IUserRepository>(sp => sp.GetRequiredService<CachedUserRepository<TUser>>());
        services.AddSingleton(sp => new CachedUserProfileRepository<TProfile>(
            StorageCalls.Wrap<IUserProfileRepository<TProfile>>(new UserProfileRepository<TProfile>(options.ProfilesStore!, options.Roles!, sp.GetService<TimeProvider>(), Log(sp, options)))));
        services.AddSingleton<IUserProfileRepository<TProfile>>(sp => sp.GetRequiredService<CachedUserProfileRepository<TProfile>>());
        services.AddSingleton<IUserProfileRepository>(sp => sp.GetRequiredService<CachedUserProfileRepository<TProfile>>());
    }

    // Local development never emails codes by default (a dev machine may hold real secrets, and casual testing must not email anyone): the code shows
    // on screen instead. AccountEmail:SendInDevelopment=true sends for real.
    private static AccountEmailOptions EmailOptions(WebApplicationBuilder builder, AccountOptions options)
    {
        var config = builder.Configuration;
        var isDevelopment = builder.Environment.IsDevelopment();
        var sendForReal = options.EmailDeliveryConfigured && (!isDevelopment || config.GetValue<bool>("AccountEmail:SendInDevelopment"));
        return new AccountEmailOptions(
            options.BrandName,
            sendForReal,
            ExposeCodeForDevelopment: isDevelopment,
            MaxPerClientPerHour: config.GetValue("AccountEmail:MaxPerClientPerHour", AccountEmailService.DefaultMaxPerClientPerHour),
            MaxPerHour: config.GetValue("AccountEmail:MaxPerHour", AccountEmailService.DefaultMaxPerHour),
            MaxPerDay: config.GetValue("AccountEmail:MaxPerDay", AccountEmailService.DefaultMaxPerDay))
        {
            ClearUnverifiedPassword = options.ClearUnverifiedPassword,
            OnConfirmationRequested = options.OnConfirmationRequested,
        };
    }

    /// <summary>The addresses in <c>Auth:AdminEmails</c> (comma-separated), lower-cased.</summary>
    public static IReadOnlySet<string> AdminEmails(IConfiguration config) =>
        (config["Auth:AdminEmails"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLowerInvariant())
            .ToHashSet();

    private static void AddCookie(IServiceCollection services, AccountOptions options)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookie =>
            {
                cookie.Cookie.Name = options.CookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.ExpireTimeSpan = options.CookieLifetime;
                cookie.SlidingExpiration = true;
                // The cookie handler treats a sign-out request to its LogoutPath (default "/Account/Logout", matched without regard to case) as one it redirects
                // itself, which swallowed this module's own redirect (200, no Location). Sign-out here is a form post answered by the endpoint, so the handler is pointed at a path nothing uses (an empty path is filled back in with the default).
                cookie.LogoutPath = "/account/cookie-handler-logout-unused";
                cookie.LoginPath = options.LoginPath;
                cookie.AccessDeniedPath = options.DeniedPath;
                cookie.Events.OnSignedIn = context => context.HttpContext.RequestServices.GetRequiredService<AccountSessionEvents>().SignedInAsync(context);
                cookie.Events.OnValidatePrincipal = context => context.HttpContext.RequestServices.GetRequiredService<AccountSessionEvents>().ValidateAsync(context);
            });
    }

    // The app may put its own wrapper around the registered sender for account mail (Better Trivia adds its unsubscribe footer); the default is the sender as registered.
    private static IEmailSender MailSender(IServiceProvider services, AccountOptions options) =>
        options.AccountMailSender?.Invoke(services) ?? services.GetRequiredService<IEmailSender>();

    private static ILogger Log(IServiceProvider services, AccountOptions options) =>
        services.GetRequiredService<ILoggerFactory>().CreateLogger(options.LogCategory);
}
