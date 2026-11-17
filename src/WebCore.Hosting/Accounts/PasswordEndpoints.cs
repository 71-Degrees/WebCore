/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using WebCore.Accounts;

namespace WebCore.Hosting.Accounts;

/// <summary>Forgot password (a code to the address, then the code and a new password; neither signs anyone in) and change password (signed in). Every
/// new password goes through <see cref="PasswordService"/>, which checks <see cref="PasswordRules"/>, ends the account's other sessions and sends a notice.</summary>
internal static class PasswordEndpoints
{
    public static void Map(RouteGroupBuilder forms)
    {
        forms.MapPost("/account/reset-send", SendResetCodeAsync);
        forms.MapPost("/account/reset-submit", ResetAsync);
        forms.MapPost("/account/password-change", ChangeAsync).RequireAuthorization();
    }

    private static async Task<IResult> SendResetCodeAsync(HttpContext http, AccountEmailService accountEmail, AccountRateLimits limits, AccountServicesBundle bundle)
    {
        var options = bundle.Options;
        var form = await http.Request.ReadFormAsync();
        var email = form["email"].ToString().Trim();
        var common = bundle.FormMemory.QueryPart(http, email);
        if (AccountEndpoints.AddressCodeLimit(http, limits, options.ForgotPasswordPath, common) is { } limited)
        {
            return limited;
        }

        var result = await accountEmail.RequestPasswordResetAsync(email, CodeClient.KeyFor(http, options));
        return result.Status == EmailRequestStatus.Sent
            ? Results.Redirect(options.ForgotPasswordPath + "?step=code" + common + AccountEndpoints.DevCode(result))
            : Results.Redirect(options.ForgotPasswordPath + "?codeError=" + AccountEndpoints.Code(result.Status) + common);
    }

    private static async Task<IResult> ResetAsync(HttpContext http, AccountEmailService accountEmail, AccountRateLimits limits, AccountServicesBundle bundle)
    {
        var options = bundle.Options;
        var form = await http.Request.ReadFormAsync();
        var email = (form["email"].ToString() is { Length: > 0 } typed ? typed : bundle.FormMemory.Recall(http) ?? string.Empty).Trim();
        var password = form["password"].ToString();
        var confirmation = form["confirmPassword"].ToString();
        var common = bundle.FormMemory.QueryPart(http, email);

        // The password is checked first, so a typo doesn't spend one of the code's five tries.
        var problem = PasswordRules.Check(password, confirmation);
        if (problem != PasswordProblem.None)
        {
            return Results.Redirect(options.ForgotPasswordPath + "?step=code&passwordError=" + PasswordRules.Code(problem) + common);
        }

        if (!limits.TryAcquire(http))
        {
            return Results.Redirect(options.ForgotPasswordPath + "?step=code&passwordError=ratelimited" + common);
        }

        var result = await accountEmail.ConfirmPasswordResetAsync(email, CodeClient.KeyFor(http, options), form["code"].ToString());
        if (result.Status != CodeCheckStatus.Confirmed)
        {
            return Results.Redirect(options.ForgotPasswordPath + "?codeError=" + AccountEndpoints.Code(result.Status) + AccountEndpoints.KeepCodeOpen(result.Status) + common);
        }

        await bundle.Passwords.ResetAsync(result.User!, password, confirmation);
        bundle.Log.LogInformation("Password reset by emailed code: {UserId}", result.User!.Id);
        return Results.Redirect(options.LoginPath + "?reset=1" + common);
    }

    // The current password is required when the account has one; an account made by emailed code (which has none) sets its first one.
    private static async Task<IResult> ChangeAsync(HttpContext http, IUserRepository users, AccountClaims claims, AccountRateLimits limits, AccountServicesBundle bundle)
    {
        var options = bundle.Options;
        var form = await http.Request.ReadFormAsync();
        var password = form["password"].ToString();
        var confirmation = form["confirmPassword"].ToString();
        var problem = PasswordRules.Check(password, confirmation);
        if (problem != PasswordProblem.None)
        {
            return Results.Redirect(options.AccountPath + "?passwordError=" + PasswordRules.Code(problem));
        }

        // Only the account's owner sets its password: an admin viewing as someone would otherwise take the account over.
        if (claims.IsMasquerading(http.User))
        {
            return Results.Redirect(options.AccountPath + "?passwordError=masquerade");
        }

        var user = await users.GetByIdAsync(AccountClaims.UserIdOf(http.User) ?? string.Empty);
        if (user is null)
        {
            return Results.Redirect(options.LoginPath);
        }

        // A guess at the current password counts against the account, as a sign-in does.
        if (!limits.TryAcquirePassword(http, user.Email))
        {
            return Results.Redirect(options.AccountPath + "?passwordError=ratelimited");
        }

        var result = string.IsNullOrEmpty(user.PasswordHash)
            ? await bundle.Passwords.SetAsync(user.Id, password, confirmation)
            : await bundle.Passwords.ChangeAsync(user.Id, form["currentPassword"].ToString(), password, confirmation);
        if (result.Status != PasswordChangeStatus.Changed)
        {
            return Results.Redirect(options.AccountPath + "?passwordError=" + ChangeErrorCode(result.Status));
        }

        // The change gave the account a new security stamp, which ends every session issued before it: this browser's included, so sign it in again.
        await bundle.SignIn.SignInAsync(http, (await users.GetByIdAsync(user.Id))!);
        bundle.Log.LogInformation("Password changed: {UserId}", user.Id);
        return Results.Redirect(options.AccountPath + "?passwordDone=1");
    }

    private static string ChangeErrorCode(PasswordChangeStatus status) => status switch
    {
        PasswordChangeStatus.WrongCurrentPassword => "wrong",
        PasswordChangeStatus.EmailNotConfirmed => "unconfirmed",
        _ => "failed",
    };
}
