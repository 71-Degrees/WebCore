/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using WebCore.Accounts;
using WebCore.Storage;

namespace WebCore.Hosting.Accounts;

/// <summary>
/// What an app tells the shared accounts module (<see cref="AccountRegistration.AddWebCoreAccounts"/>). Names that are read back from live cookies, logs or
/// storage (the cookie names, the claim prefix, the log category, the stores) must keep the values the app already uses: changing one signs everybody out,
/// hides their confirmed emails or makes accounts appear to vanish.
/// </summary>
public sealed class AccountOptions
{
    /// <summary>The site's name in code and notice emails ("Your CrateDigger code").</summary>
    public string BrandName { get; set; } = string.Empty;

    /// <summary>The roles the app knows; required.</summary>
    public AccountRoles? Roles { get; set; }

    /// <summary>Where accounts are stored (one document per email); required.</summary>
    public IDocumentStore? UsersStore { get; set; }

    /// <summary>Where profiles are stored (one document per user id); required.</summary>
    public IDocumentStore? ProfilesStore { get; set; }

    /// <summary>True when the app registered a real <see cref="Email.IEmailSender"/> before calling AddWebCoreAccounts. Without one, a sender that only
    /// logs is registered and codes are not emailed (in development they show on screen).</summary>
    public bool EmailDeliveryConfigured { get; set; }

    public string CookieName { get; set; } = "webcore_auth";

    /// <summary>Binds an emailed sign-in code to the browser that asked for it.</summary>
    public string CodeClientCookieName { get; set; } = "webcore_code_client";

    /// <summary>Prefix of the app's own claims (<see cref="AccountClaims"/>), for example <c>cratedigger:</c>.</summary>
    public string ClaimPrefix { get; set; } = "webcore:";

    /// <summary>The logger category of every account log line; alert queries key on it.</summary>
    public string LogCategory { get; set; } = "WebCore.Accounts";

    public TimeSpan CookieLifetime { get; set; } = TimeSpan.FromDays(14);

    public string LoginPath { get; set; } = "/account/login";

    public string RegisterPath { get; set; } = "/account/register";

    public string ForgotPasswordPath { get; set; } = "/account/forgot";

    public string DeniedPath { get; set; } = "/account/denied";

    /// <summary>The signed-in account page (confirm email, change password).</summary>
    public string AccountPath { get; set; } = "/account";

    /// <summary>The admin users page, and the prefix of its form posts (<c>/role</c>, <c>/masquerade</c>, <c>/disabled</c>).</summary>
    public string AdminUsersPath { get; set; } = "/admin/users";

    /// <summary>Where an admin lands after starting a masquerade.</summary>
    public string MasqueradeLandingPath { get; set; } = "/";

    internal void Validate()
    {
        if (Roles is null || UsersStore is null || ProfilesStore is null || string.IsNullOrWhiteSpace(BrandName))
        {
            throw new InvalidOperationException("AddWebCoreAccounts needs BrandName, Roles, UsersStore and ProfilesStore.");
        }
    }
}
