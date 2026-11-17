/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using WebCore.Accounts;
using WebCore.Hosting.Security;

namespace WebCore.Hosting.Accounts;

/// <summary>
/// The account form endpoints. Plain endpoints rather than Blazor components because signing in has to write the auth cookie. Each one is on a form group
/// (antiforgery checked before the handler), re-checks everything server-side, and redirects back to a page with a short status in the query string. The
/// paths differ from the pages' own (<c>/account/login</c> vs <c>/account/login-submit</c>) because a page and an endpoint on one POST path are ambiguous.
/// The paths and form field names are a contract with each app's pages (and with pages open during a deploy): keep them.
/// </summary>
public static class AccountEndpoints
{
    public static void MapWebCoreAccountEndpoints(this WebApplication app)
    {
        var forms = app.MapFormGroup();
        forms.MapPost("/account/logout", LogoutAsync);
        forms.MapPost("/account/register-submit", RegisterAsync);
        forms.MapPost("/account/login-submit", LoginAsync);
        forms.MapPost("/account/code-send", SendSignInCodeAsync);
        forms.MapPost("/account/code-verify", VerifySignInCodeAsync);

        // Signed in: send a code to confirm the current email, or to a new address to change it.
        forms.MapPost("/account/email-send", SendEmailCodeAsync).RequireAuthorization();
        forms.MapPost("/account/email-verify", VerifyEmailCodeAsync).RequireAuthorization();

        // Resume: open to whoever is signed in, because while masquerading the principal is the target's (often not an admin).
        forms.MapPost("/account/masquerade-stop", StopMasqueradeAsync).RequireAuthorization();

        PasswordEndpoints.Map(forms);
        AdminUserEndpoints.Map(app);
    }

    private static async Task<IResult> LogoutAsync(HttpContext http)
    {
        await AccountSignIn.SignOutAsync(http);
        return Results.LocalRedirect("/");
    }

    private static async Task<IResult> RegisterAsync(HttpContext http, IUserRepository users, AccountRateLimits limits, AccountServicesBundle bundle)
    {
        var options = bundle.Options;
        var form = await http.Request.ReadFormAsync();
        var rawEmail = form["email"].ToString();
        var displayName = DisplayNames.Tidy(form["displayName"].ToString());
        var password = form["password"].ToString();
        var returnUrl = LocalReturnUrl.Sanitize(form["returnUrl"].ToString());

        IResult Fail(string reason) => Results.Redirect(
            $"{options.RegisterPath}?error={Uri.EscapeDataString(reason)}&returnUrl={Uri.EscapeDataString(returnUrl)}" +
            $"{bundle.FormMemory.QueryPart(http, rawEmail.Trim())}&displayName={Uri.EscapeDataString(displayName)}");

        var email = AccountEmailService.NormalizeEmail(rawEmail);
        if (email is null)
        {
            return Fail("Please enter a valid email address.");
        }

        if (!DisplayNames.IsValid(displayName))
        {
            return Fail($"Please enter a display name of up to {DisplayNames.MaxLength} characters.");
        }

        var problem = PasswordRules.Validate(password, form["confirmPassword"].ToString());
        if (problem is not null)
        {
            return Fail(problem);
        }

        // The app's own screening goes before the shared attempt limiter, so refused bots cannot use up the attempts real people share.
        if (bundle.SignupPolicy.Refusal(http, email, displayName) is { } refusal)
        {
            return Fail(refusal);
        }

        if (!limits.TryAcquire(http))
        {
            return Fail("Too many attempts right now - please try again in a few minutes.");
        }

        var user = new User { Email = email, DisplayName = displayName, PasswordHash = PasswordHasher.Hash(password) };
        try
        {
            // The stored account: an app's own account class is a copy of the plain one made above, and the signup policy must see (and may change) that one.
            user = await users.CreateAsync(user);
        }
        catch (InvalidOperationException)
        {
            return Fail("That email is already registered - try signing in instead.");
        }

        bundle.Log.LogInformation("New account registered: {UserId}", user.Id);
        await bundle.SignupPolicy.AccountCreatedAsync(http, user, form);
        await bundle.SignIn.SignInAsync(http, user);
        return Results.LocalRedirect(returnUrl);
    }

    /// <summary>Password sign-in. Every refusal answers the visitor the same way; the log gets the real reason (see <see cref="SignInDiagnostics"/>).</summary>
    private static async Task<IResult> LoginAsync(HttpContext http, IUserRepository users, IUserProfileRepository profiles, AccountRateLimits limits, AccountServicesBundle bundle)
    {
        var log = bundle.Log;
        var form = await http.Request.ReadFormAsync();
        var rawEmail = form["email"].ToString().Trim();
        var password = form["password"].ToString();
        var returnUrl = LocalReturnUrl.Sanitize(form["returnUrl"].ToString());
        IResult Refuse(string error) => Results.Redirect($"{bundle.Options.LoginPath}?error={error}&returnUrl={Uri.EscapeDataString(returnUrl)}{bundle.FormMemory.QueryPart(http, rawEmail)}");

        if (!limits.TryAcquirePassword(http, rawEmail))
        {
            SignInDiagnostics.Refused(log, "rate limited", rawEmail);
            return Refuse("ratelimited");
        }

        var email = AccountEmailService.NormalizeEmail(rawEmail);
        var user = email is null ? null : await users.GetByEmailAsync(email);

        // The same answer for "no such account", "no password on this account" and "wrong password": telling them apart would let this endpoint probe
        // which emails are registered. That includes the time taken: with no hash to check, the hasher spends the same time on a decoy.
        if (string.IsNullOrEmpty(user?.PasswordHash))
        {
            PasswordHasher.SpendVerifyTime(password);
            SignInDiagnostics.Refused(log, user is null ? "no such account" : "account has no password", rawEmail);
            return Refuse("1");
        }

        if (!PasswordHasher.Verify(password, user.PasswordHash))
        {
            SignInDiagnostics.Refused(log, "wrong password", rawEmail);
            return Refuse("1");
        }

        // Only reachable with the right password, so naming the real reason leaks nothing.
        if ((await profiles.GetAsync(user.Id)) is { IsDisabled: true })
        {
            SignInDiagnostics.Refused(log, "account disabled", rawEmail);
            return Refuse("disabled");
        }

        await bundle.Passwords.UpgradeHashIfNeededAsync(user, password);
        await bundle.SignIn.SignInAsync(http, user);
        log.LogInformation("Signed in with a password: {UserId}", user.Id);
        return Results.LocalRedirect(returnUrl);
    }

    private static async Task<IResult> SendSignInCodeAsync(HttpContext http, AccountEmailService accountEmail, AccountServicesBundle bundle)
    {
        var options = bundle.Options;
        var form = await http.Request.ReadFormAsync();
        var email = form["email"].ToString().Trim();
        var returnUrl = LocalReturnUrl.Sanitize(form["returnUrl"].ToString());

        var result = await accountEmail.RequestSignInCodeAsync(email, CodeClient.KeyFor(http, options));
        var common = $"{bundle.FormMemory.QueryPart(http, email)}&returnUrl={Uri.EscapeDataString(returnUrl)}";
        return result.Status == EmailRequestStatus.Sent
            ? Results.Redirect(options.LoginPath + "?step=code" + common + DevCode(result))
            : Results.Redirect(options.LoginPath + "?codeError=" + Code(result.Status) + common);
    }

    private static async Task<IResult> VerifySignInCodeAsync(HttpContext http, AccountEmailService accountEmail, IUserProfileRepository profiles, AccountServicesBundle bundle)
    {
        var options = bundle.Options;
        var signIn = bundle.SignIn;
        var form = await http.Request.ReadFormAsync();
        var email = (form["email"].ToString() is { Length: > 0 } typed ? typed : bundle.FormMemory.Recall(http) ?? string.Empty).Trim();
        var returnUrl = LocalReturnUrl.Sanitize(form["returnUrl"].ToString());
        var common = $"{bundle.FormMemory.QueryPart(http, email)}&returnUrl={Uri.EscapeDataString(returnUrl)}";

        var clientKey = http.Request.Cookies[options.CodeClientCookieName];
        if (string.IsNullOrWhiteSpace(clientKey))
        {
            return Results.Redirect(options.LoginPath + "?codeError=expired" + common);
        }

        var result = await accountEmail.ConfirmSignInAsync(email, clientKey, form["code"].ToString(), form["displayName"].ToString());
        switch (result.Status)
        {
            case CodeCheckStatus.Confirmed:
                if ((await profiles.GetAsync(result.User!.Id)) is { IsDisabled: true })
                {
                    return Results.Redirect(options.LoginPath + "?error=disabled" + common);
                }

                if (result.IsNewAccount)
                {
                    await bundle.SignupPolicy.AccountCreatedAsync(http, result.User!, form);
                }

                await signIn.SignInAsync(http, result.User!);
                http.Response.Cookies.Delete(options.CodeClientCookieName);
                return Results.LocalRedirect(returnUrl);
            case CodeCheckStatus.UsePassword:
                return Results.Redirect(options.LoginPath + "?error=usepassword" + common);
            default:
                return Results.Redirect(options.LoginPath + "?codeError=" + Code(result.Status) + KeepCodeOpen(result.Status) + common);
        }
    }

    private static async Task<IResult> SendEmailCodeAsync(HttpContext http, AccountEmailService accountEmail, AccountServicesBundle bundle)
    {
        var options = bundle.Options;
        var form = await http.Request.ReadFormAsync();
        var email = form["email"].ToString().Trim();
        var result = await accountEmail.RequestEmailConfirmationAsync(AccountClaims.UserIdOf(http.User), email);
        var common = bundle.FormMemory.QueryPart(http, email);
        return result.Status == EmailRequestStatus.Sent
            ? Results.Redirect(options.AccountPath + "?step=code" + common + DevCode(result))
            : Results.Redirect(options.AccountPath + "?emailError=" + Code(result.Status) + common);
    }

    // Confirming re-issues the cookie, so the email-confirmed claim (and a changed address) apply at once, and a configured admin email is promoted by
    // the sign-in check the moment it is proven.
    private static async Task<IResult> VerifyEmailCodeAsync(HttpContext http, AccountEmailService accountEmail, AccountServicesBundle bundle)
    {
        var options = bundle.Options;
        var signIn = bundle.SignIn;
        var form = await http.Request.ReadFormAsync();
        var common = bundle.FormMemory.QueryPart(http, form["email"].ToString().Trim());
        var result = await accountEmail.ConfirmEmailAsync(AccountClaims.UserIdOf(http.User), form["code"].ToString());
        if (result.Status != CodeCheckStatus.Confirmed)
        {
            return Results.Redirect(options.AccountPath + "?emailError=" + Code(result.Status) + KeepCodeOpen(result.Status) + common);
        }

        await signIn.SignInAsync(http, result.User!);
        return Results.Redirect(options.AccountPath + "?confirmed=1");
    }

    private static async Task<IResult> StopMasqueradeAsync(HttpContext http, IUserRepository users, AccountClaims claims, AccountServicesBundle bundle)
    {
        var originalAdminId = http.User.FindFirst(claims.MasqueradeOriginalAdminId)?.Value;
        if (originalAdminId is null)
        {
            return Results.LocalRedirect("/");
        }

        var admin = await users.GetByIdAsync(originalAdminId);
        if (admin is null)
        {
            // The admin account is gone: sign out rather than stay signed in as the person being viewed.
            await AccountSignIn.SignOutAsync(http);
            return Results.LocalRedirect("/");
        }

        await bundle.SignIn.SignInAsync(http, admin);
        bundle.Log.LogInformation("Admin {AdminId} resumed their own identity", originalAdminId);
        return Results.LocalRedirect(bundle.Options.AdminUsersPath);
    }

    /// <summary>The status as the lower-case code the pages read back (<see cref="AccountMessages"/>).</summary>
    internal static string Code<TStatus>(TStatus status) where TStatus : struct, Enum => status.ToString().ToLowerInvariant();

    /// <summary>A wrong code keeps the code box open; an expired or over-tried one goes back to asking for a new code.</summary>
    internal static string KeepCodeOpen(CodeCheckStatus status) => status == CodeCheckStatus.WrongCode ? "&step=code" : string.Empty;

    internal static string DevCode(EmailRequestResult result) => result.DevCode is null ? string.Empty : "&devCode=" + result.DevCode;
}

/// <summary>The services several account handlers share, so a handler's parameter list stays short (one request-scoped bundle, not six injections).</summary>
public sealed class AccountServicesBundle(
    AccountOptions options, AccountSignIn signIn, PasswordService passwords, AccountFormMemory formMemory, IAccountSignupPolicy signupPolicy, ILoggerFactory loggers)
{
    public AccountOptions Options { get; } = options;

    public AccountSignIn SignIn { get; } = signIn;

    public PasswordService Passwords { get; } = passwords;

    public AccountFormMemory FormMemory { get; } = formMemory;

    public IAccountSignupPolicy SignupPolicy { get; } = signupPolicy;

    public ILogger Log { get; } = loggers.CreateLogger(options.LogCategory);
}

/// <summary>The cookie that binds an emailed code to the browser that asked for it (set on first use, for an hour).</summary>
internal static class CodeClient
{
    public static string KeyFor(HttpContext http, AccountOptions options)
    {
        var clientKey = http.Request.Cookies[options.CodeClientCookieName];
        if (!string.IsNullOrWhiteSpace(clientKey))
        {
            return clientKey;
        }

        clientKey = Guid.NewGuid().ToString("N");
        http.Response.Cookies.Append(options.CodeClientCookieName, clientKey, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            MaxAge = TimeSpan.FromHours(1),
        });
        return clientKey;
    }
}
