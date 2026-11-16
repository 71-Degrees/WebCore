/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using WebCore.Accounts;

namespace WebCore.Hosting.Accounts;

public enum SessionVerdict
{
    Keep,

    /// <summary>The stored role differs from the cookie's: re-issue the cookie with the stored role, so a promotion or demotion applies without signing out.</summary>
    RefreshRole,

    /// <summary>No profile, a disabled one, or a password changed since the cookie was issued: sign the visitor out.</summary>
    Reject,
}

/// <summary>
/// The sign-in cookie's two checks. After every sign-in, whichever path produced it: make sure a profile exists, stop a disabled account, and promote an
/// address in <c>Auth:AdminEmails</c> to admin, only once it is CONFIRMED (registering with an admin's address without proving it is never enough).
/// On every request carrying the cookie: fail closed on a missing or disabled profile or a stale security stamp, and keep the role claim in step with the
/// stored role. Both reads are cached (at most 30 seconds, cleared at once by a change made through the app).
/// </summary>
public sealed class AccountSessionEvents(AccountOptions options, IReadOnlySet<string> adminEmails)
{
    public async Task SignedInAsync(CookieSignedInContext context)
    {
        var principal = context.Principal;
        var userId = principal is null ? null : AccountClaims.UserIdOf(principal);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        var services = context.HttpContext.RequestServices;
        var profiles = services.GetRequiredService<IUserProfileRepository>();
        var profile = await profiles.EnsureProfileAsync(userId, principal!.Identity?.Name ?? string.Empty).ConfigureAwait(false);
        if (profile.IsDisabled)
        {
            // The sign-in endpoints refuse a disabled account before getting here; this is the backstop.
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            return;
        }

        if (ShouldBecomeAdmin(principal, profile, services.GetRequiredService<AccountClaims>()))
        {
            // The role claim catches up on the next request, in ValidateAsync.
            await profiles.SetRoleAsync(userId, options.Roles!.Admin).ConfigureAwait(false);
        }
    }

    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal!;
        var services = context.HttpContext.RequestServices;
        var (profile, currentStamp) = await ReadAccountAsync(context, AccountClaims.UserIdOf(principal)).ConfigureAwait(false);
        switch (Judge(principal, profile, currentStamp, services.GetRequiredService<AccountClaims>()))
        {
            case SessionVerdict.Reject:
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
                break;
            case SessionVerdict.RefreshRole:
                context.ReplacePrincipal(AccountClaims.WithRole(principal, profile!.Role, CookieAuthenticationDefaults.AuthenticationScheme));
                context.ShouldRenew = true;
                break;
        }
    }

    /// <summary>What to do with a cookie, given the account as stored now. Both stamps null is the case of an account whose password never changed since
    /// stamps existed (and a cookie issued before them): it stays valid, so a deploy signs nobody out.</summary>
    public static SessionVerdict Judge(ClaimsPrincipal principal, UserProfile? profile, string? currentStamp, AccountClaims claims)
    {
        if (profile is null or { IsDisabled: true })
        {
            return SessionVerdict.Reject;
        }

        if (!string.Equals(principal.FindFirst(claims.SecurityStamp)?.Value, currentStamp, StringComparison.Ordinal))
        {
            return SessionVerdict.Reject;
        }

        return string.Equals(AccountClaims.RoleOf(principal), profile.Role, StringComparison.Ordinal) ? SessionVerdict.Keep : SessionVerdict.RefreshRole;
    }

    private bool ShouldBecomeAdmin(ClaimsPrincipal principal, UserProfile profile, AccountClaims claims)
    {
        var email = principal.FindFirst(ClaimTypes.Email)?.Value;
        return !options.Roles!.IsAdmin(profile.Role) && claims.IsEmailConfirmed(principal) && !string.IsNullOrWhiteSpace(email)
            && adminEmails.Contains(email.ToLowerInvariant());
    }

    private async Task<(UserProfile? Profile, string? Stamp)> ReadAccountAsync(CookieValidatePrincipalContext context, string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return (null, null);
        }

        var services = context.HttpContext.RequestServices;
        try
        {
            var profile = await services.GetRequiredService<IUserProfileRepository>().GetAsync(userId).ConfigureAwait(false);
            var user = profile is null ? null : await services.GetRequiredService<IUserRepository>().GetByIdAsync(userId).ConfigureAwait(false);
            return (profile, user?.SecurityStamp);
        }
        catch (ArgumentException ex)
        {
            // An id that can't be a profile key is not a valid session.
            Log(services).LogDebug(ex, "A session cookie carried an id that is not a valid profile key; rejecting it");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Storage still unreadable after the repository's retries: fail closed (sign out), not a 500.
            Log(services).LogWarning(ex, "The account store could not be read while validating a session; signing the visitor out");
        }

        return (null, null);
    }

    private ILogger Log(IServiceProvider services) => services.GetRequiredService<ILoggerFactory>().CreateLogger(options.LogCategory);
}
