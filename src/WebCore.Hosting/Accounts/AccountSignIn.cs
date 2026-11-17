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
/// Limits on registration, password sign-in, password changes and resets, backed by <see cref="AccountAttemptLimiter"/>: 10 per 10 minutes from one address,
/// and for a password, 10 per 15 minutes against one account from anywhere (so a password cannot be guessed at speed from many addresses), then a shared
/// ceiling. The narrow limits are checked first, so one noisy client spends its own allowance and not everyone's. Emailed codes have their own, stricter
/// limits inside <see cref="AccountEmailService"/>.
/// </summary>
public sealed class AccountRateLimits
{
    private readonly AccountAttemptLimiter _limits;

    public AccountRateLimits(AccountOptions options) =>
        _limits = new AccountAttemptLimiter(scale: options.AttemptScale, globalMax: options.SharedAttemptCeiling);

    /// <summary>An attempt that names no existing password (a registration, a reset by emailed code): this address's allowance, then the shared ceiling.</summary>
    public bool TryAcquire(HttpContext http) => _limits.TryAcquire(Address(http));

    /// <summary>An attempt at <paramref name="email"/>'s password (a sign-in, a password change): that account's allowance, then this address's, then the shared ceiling.</summary>
    public bool TryAcquirePassword(HttpContext http, string email) => _limits.TryAcquireLogin(Address(http), email);

    /// <summary>A request for an emailed code (a send, a check) or a reset: only this address's allowance. The code service has its own per-email limits behind it.</summary>
    public bool TryAcquireAddress(HttpContext http) => _limits.TryAcquireAddress(Address(http));

    // Requests with no address (an in-memory test host) share one allowance.
    private static string Address(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
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
