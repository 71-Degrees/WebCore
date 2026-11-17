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

    /// <summary>True: every sign-in is remembered (a persistent cookie that survives closing the browser, for <see cref="CookieLifetime"/>). False, the
    /// default: the cookie lasts as long as the browser session.</summary>
    public bool PersistentSignIn { get; set; }

    /// <summary>True: the address a person typed travels across a redirect in a sealed, ten-minute cookie (<see cref="AccountFormMemory"/>) instead of an
    /// <c>email=</c> query parameter, so it never lands in request logs or telemetry. The pages read it with <see cref="AccountFormMemory.Recall"/>.
    /// False, the default: the address is in the query string.</summary>
    public bool KeepEmailsOutOfUrls { get; set; }

    /// <summary>The cookie <see cref="AccountFormMemory"/> uses.</summary>
    public string FormEmailCookieName { get; set; } = "webcore_form_email";

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

    /// <summary>True: an account with a password that never proved its email loses the password when its owner proves the address by code. False, the
    /// default, keeps it (see <see cref="AccountEmailOptions.ClearUnverifiedPassword"/>).</summary>
    public bool ClearUnverifiedPassword { get; set; }

    /// <summary>Called with the account and the answer each time a confirmation code is sent or fails to send, so the app can record it on the account.
    /// Null, the default, records nothing (see <see cref="AccountEmailOptions.OnConfirmationRequested"/>).</summary>
    public Action<User, EmailRequestStatus>? OnConfirmationRequested { get; set; }

    /// <summary>Builds the sender used for sign-in codes and password notices, when the app wraps its registered <see cref="Email.IEmailSender"/> for account
    /// mail (a footer, a mail kind). Null, the default, uses the registered sender as it is.</summary>
    public Func<IServiceProvider, Email.IEmailSender>? AccountMailSender { get; set; }

    /// <summary>The shared ceiling on registrations and password attempts per 10 minutes, across every address. The per-address and per-account limits are
    /// the real protection; this is a backstop against a mass attack. Default 60 (what every app had before the module). An app with a real audience may
    /// raise it so a few noisy clients cannot lock everyone out.</summary>
    public int SharedAttemptCeiling { get; set; } = 60;

    /// <summary>Multiplies every attempt limit (the shared ceiling and the per-address and per-account ones). Default 1 (production). A development machine
    /// sets it higher, because its browser tests all come from one address and would otherwise lock themselves out.</summary>
    public int AttemptScale { get; set; } = 1;

    internal void Validate()
    {
        if (Roles is null || UsersStore is null || ProfilesStore is null || string.IsNullOrWhiteSpace(BrandName))
        {
            throw new InvalidOperationException("AddWebCoreAccounts needs BrandName, Roles, UsersStore and ProfilesStore.");
        }
    }
}
