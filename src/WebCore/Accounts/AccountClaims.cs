/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using System.Security.Claims;

namespace WebCore.Accounts;

/// <summary>
/// The one place an account becomes a login: every sign-in path (password, emailed code, masquerade, a dev shortcut) builds its principal here, so every
/// check elsewhere reads the same claims. The app's own claim names carry its prefix (<c>cratedigger:</c>), because they are read back from cookies already
/// issued: a changed name would hide a confirmed email until the next sign-in and stop a masquerade ending cleanly.
/// </summary>
public sealed class AccountClaims
{
    public AccountClaims(string claimPrefix)
    {
        EmailConfirmed = claimPrefix + "email_confirmed";
        MasqueradeOriginalAdminId = claimPrefix + "masquerade_admin";
        SecurityStamp = claimPrefix + "security_stamp";
    }

    /// <summary>Present (value "1") only when the account's email has been confirmed by a code.</summary>
    public string EmailConfirmed { get; }

    /// <summary>Present only while an Admin is masquerading as this account: holds the ORIGINAL admin's user id, so "Resume as yourself" can sign back in
    /// without a fresh login.</summary>
    public string MasqueradeOriginalAdminId { get; }

    /// <summary>The account's <see cref="User.SecurityStamp"/> when the cookie was issued; absent when the account had none.</summary>
    public string SecurityStamp { get; }

    /// <param name="role">The account's current role, issued as a <see cref="ClaimTypes.Role"/> claim. The cookie check re-issues the principal whenever
    /// the stored role changes, so a promotion or demotion takes effect without signing out.</param>
    public ClaimsPrincipal CreatePrincipal(User user, string role, string authenticationType)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, role),
        };
        if (user.EmailVerifiedAt is not null)
        {
            claims.Add(new Claim(EmailConfirmed, "1"));
        }

        if (!string.IsNullOrEmpty(user.SecurityStamp))
        {
            claims.Add(new Claim(SecurityStamp, user.SecurityStamp));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
    }

    /// <summary>The claims <see cref="CreatePrincipal"/> builds for <paramref name="targetUser"/> (so every check sees them exactly as they would see
    /// themselves, role included), plus the claim that makes this a masquerade.</summary>
    public ClaimsPrincipal CreateMasqueradePrincipal(User targetUser, string targetRole, string originalAdminUserId, string authenticationType)
    {
        var principal = CreatePrincipal(targetUser, targetRole, authenticationType);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(MasqueradeOriginalAdminId, originalAdminUserId));
        return principal;
    }

    /// <summary>True while an Admin is viewing the site as someone else.</summary>
    public bool IsMasquerading(ClaimsPrincipal principal) => principal.HasClaim(c => c.Type == MasqueradeOriginalAdminId);

    public bool IsEmailConfirmed(ClaimsPrincipal principal) => principal.HasClaim(EmailConfirmed, "1");

    /// <summary>A copy of <paramref name="principal"/> whose role claim is exactly <paramref name="role"/>, every other claim kept as is.</summary>
    public static ClaimsPrincipal WithRole(ClaimsPrincipal principal, string role, string authenticationType)
    {
        var claims = principal.Claims.Where(c => c.Type != ClaimTypes.Role).Append(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
    }

    /// <summary>The role claim, or null when the principal has none.</summary>
    public static string? RoleOf(ClaimsPrincipal principal) => principal.FindFirst(ClaimTypes.Role)?.Value;

    public static string? UserIdOf(ClaimsPrincipal principal) => principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}
