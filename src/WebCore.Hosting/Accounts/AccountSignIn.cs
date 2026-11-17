/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using WebCore.Accounts;
using WebCore.RateLimiting;

namespace WebCore.Hosting.Accounts;

/// <summary>Signs a browser in as an account with its current role. Every sign-in path (password, emailed code, masquerade, a dev shortcut) goes through
/// here, so they all issue the same claims.</summary>
public sealed class AccountSignIn(IUserProfileRepository profiles, AccountClaims claims, AccountOptions options, AccountFormMemory formMemory)
{
    public async Task<ClaimsPrincipal> SignInAsync(HttpContext http, User user)
    {
        var role = (await profiles.GetAsync(user.Id).ConfigureAwait(false))?.Role ?? options.Roles!.Default;
        var principal = claims.CreatePrincipal(user, role, CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, Properties()).ConfigureAwait(false);
        if (options.KeepEmailsOutOfUrls)
        {
            // Signed in: the address typed on the way here is no longer needed.
            formMemory.Forget(http);
        }

        return principal;
    }

    /// <summary>Signs an admin in as <paramref name="target"/>, remembering the admin so "Resume as yourself" can return without a fresh login.</summary>
    public Task SignInAsMasqueradeAsync(HttpContext http, User target, string targetRole, string originalAdminUserId) =>
        http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            claims.CreateMasqueradePrincipal(target, targetRole, originalAdminUserId, CookieAuthenticationDefaults.AuthenticationScheme),
            Properties());

    // A remembered sign-in (AccountOptions.PersistentSignIn) survives closing the browser; otherwise the cookie lasts the browser session.
    private AuthenticationProperties Properties() => new() { IsPersistent = options.PersistentSignIn };

    public static Task SignOutAsync(HttpContext http) => http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
}

/// <summary>
/// Limits on password sign-in, registration and password changes: per caller (IP address) AND a global ceiling, so neither one script nor a crowd of them
/// can grind through passwords. Emailed codes have their own, stricter limits inside <see cref="AccountEmailService"/>.
/// </summary>
public sealed class AccountRateLimits
{
    private const int MaxTrackedCallers = 5000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly SlidingWindowRateLimiter _global = new(maxInWindow: 60, Window);
    private readonly ConcurrentDictionary<string, SlidingWindowRateLimiter> _perCaller = new();

    /// <summary>True when this attempt may go ahead. The global counter is only spent once the caller's own limit allows it.</summary>
    public bool TryAcquire(HttpContext http)
    {
        if (_perCaller.Count > MaxTrackedCallers)
        {
            _perCaller.Clear();
        }

        var caller = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return _perCaller.GetOrAdd(caller, _ => new SlidingWindowRateLimiter(maxInWindow: 10, Window)).TryAcquire()
            && _global.TryAcquire();
    }
}

/// <summary>
/// Why a password sign-in was refused, for the server log only. The visitor always gets the same "wrong email or password" answer (so the form cannot probe
/// which emails exist); the operator needs the real reason to diagnose a sign-in that works on one device and not another. The address is never logged: a
/// short fingerprint of it lets repeated attempts be matched without storing who they were for.
/// </summary>
public static class SignInDiagnostics
{
    public static void Refused(ILogger log, string reason, string rawEmail) =>
        log.LogInformation("Sign-in refused: {Reason} (email fingerprint {EmailFingerprint})", reason, EmailFingerprint(rawEmail));

    /// <summary>The first 8 hex characters of the SHA-256 of the trimmed, lower-cased address.</summary>
    public static string EmailFingerprint(string rawEmail)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawEmail.Trim().ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}
