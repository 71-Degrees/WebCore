/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using WebCore.Accounts;
using WebCore.Hosting.Security;

namespace WebCore.Hosting.Accounts;

/// <summary>The admin users page's form posts: set a role, disable or re-enable an account, and masquerade (see the site as that account does). An
/// admin can never act on themself: that is the only way to lose the last admin by accident.</summary>
internal static class AdminUserEndpoints
{
    public static void Map(WebApplication app)
    {
        var options = app.Services.GetRequiredService<AccountOptions>();
        var admin = app.MapFormGroup(options.AdminUsersPath).RequireAuthorization(AccountPolicies.Admin);
        admin.MapPost("/role", SetRoleAsync);
        admin.MapPost("/masquerade", MasqueradeAsync);
        admin.MapPost("/disabled", SetDisabledAsync);
    }

    private static async Task<IResult> SetRoleAsync(HttpContext http, IUserProfileRepository profiles, AccountRoles roles, AccountServicesBundle bundle)
    {
        var back = bundle.Options.AdminUsersPath;
        var form = await http.Request.ReadFormAsync();
        var target = form["userId"].ToString();
        var role = form["role"].ToString();
        if (!roles.IsKnown(role))
        {
            return Results.Redirect(back + "?error=badrole");
        }

        var refusal = await RefuseTargetAsync(http, profiles, target, back);
        if (refusal is not null)
        {
            return refusal;
        }

        await profiles.SetRoleAsync(target, role);
        bundle.Log.LogInformation("Admin {AdminId} set {UserId} to {Role}", AccountClaims.UserIdOf(http.User), target, role);
        return Results.Redirect(back + "?done=role");
    }

    // The role and ownership checks read the claims (the role re-issued from the profile store each request), so signing in as the target is enough.
    private static async Task<IResult> MasqueradeAsync(HttpContext http, IUserRepository users, IUserProfileRepository profiles, AccountClaims claims, AccountServicesBundle bundle)
    {
        var back = bundle.Options.AdminUsersPath;
        var form = await http.Request.ReadFormAsync();
        var targetId = form["userId"].ToString();
        var adminId = AccountClaims.UserIdOf(http.User);
        var adminProfile = adminId is null ? null : await TryGetProfileAsync(profiles, adminId);
        if (!bundle.Options.Roles!.IsAdmin(adminProfile?.Role))
        {
            return Results.Forbid();
        }

        // Resume first: stacking one masquerade on another would lose track of who the real admin is.
        if (claims.IsMasquerading(http.User))
        {
            return Results.Redirect(back + "?error=already");
        }

        if (IsSelf(http, targetId))
        {
            return Results.Redirect(back + "?error=self");
        }

        var targetProfile = await TryGetProfileAsync(profiles, targetId);
        var target = targetProfile is null ? null : await users.GetByIdAsync(targetId);
        if (targetProfile is null || target is null)
        {
            return Results.Redirect(back + "?error=unknown");
        }

        if (targetProfile.IsDisabled)
        {
            return Results.Redirect(back + "?error=disabled");
        }

        await bundle.SignIn.SignInAsMasqueradeAsync(http, target, targetProfile.Role, adminId!);

        // Warning, not Information: loud enough to reach the alert digest, so every masquerade leaves a visible trail.
        bundle.Log.LogWarning("Admin {AdminId} started masquerading as {TargetId} ({TargetEmail})", adminId, targetId, target.Email);
        return Results.LocalRedirect(bundle.Options.MasqueradeLandingPath);
    }

    private static async Task<IResult> SetDisabledAsync(HttpContext http, IUserProfileRepository profiles, AccountServicesBundle bundle)
    {
        var back = bundle.Options.AdminUsersPath;
        var form = await http.Request.ReadFormAsync();
        var target = form["userId"].ToString();
        var disable = form["disabled"].ToString() == "true";
        var refusal = await RefuseTargetAsync(http, profiles, target, back);
        if (refusal is not null)
        {
            return refusal;
        }

        await profiles.SetDisabledAsync(target, disable);
        bundle.Log.LogInformation("Admin {AdminId} {Action} {UserId}", AccountClaims.UserIdOf(http.User), disable ? "disabled" : "re-enabled", target);
        return Results.Redirect(back + "?done=" + (disable ? "disabled" : "enabled"));
    }

    /// <summary>The redirect that refuses a role or disable change, or null when <paramref name="target"/> is another account that has a profile.</summary>
    private static async Task<IResult?> RefuseTargetAsync(HttpContext http, IUserProfileRepository profiles, string target, string back)
    {
        if (IsSelf(http, target))
        {
            return Results.Redirect(back + "?error=self");
        }

        return await TryGetProfileAsync(profiles, target) is null ? Results.Redirect(back + "?error=unknown") : null;
    }

    private static bool IsSelf(HttpContext http, string userId) => string.Equals(AccountClaims.UserIdOf(http.User), userId, StringComparison.Ordinal);

    /// <summary>The profile, or null for an unknown or malformed id (never an exception from a tampered form).</summary>
    private static async Task<UserProfile?> TryGetProfileAsync(IUserProfileRepository profiles, string userId)
    {
        try
        {
            return string.IsNullOrWhiteSpace(userId) ? null : await profiles.GetAsync(userId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
