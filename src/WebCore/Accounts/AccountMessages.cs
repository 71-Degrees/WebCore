/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
namespace WebCore.Accounts;

/// <summary>What to tell a person for each status code the account form endpoints put in the query string.</summary>
public static class AccountMessages
{
    public static string ForPasswordError(string? status) => status switch
    {
        "short" => $"A password needs at least {PasswordRules.MinimumLength} characters.",
        "long" => $"A password can be at most {PasswordRules.MaximumLength} characters.",
        "mismatch" => "The two passwords don't match.",
        "wrong" => "That isn't your current password.",
        "unconfirmed" => "Confirm your email first, then set a password.",
        "masquerade" => "Only the account's owner can set its password.",
        "ratelimited" => "Too many tries - please wait a few minutes and try again.",
        _ => "That didn't work - please try again.",
    };

    public static string ForCodeError(string? status) => status switch
    {
        "invalidemail" => "That doesn't look like a valid email address.",
        "ratelimited" => "Too many tries - please wait a little while and try again.",
        "sendfailed" => "We couldn't send the email just now - please try again in a minute.",
        "unavailable" => "Emailed codes aren't available right now - please use a password.",
        "wrongcode" => "That code isn't right - check it and try again.",
        "toomanyattempts" => "Too many wrong tries. Ask for a new code.",
        "emailinuse" => "That email already belongs to another account.",
        "notsignedin" => "Please sign in again.",
        _ => "That code has expired. Ask for a new one.",
    };
}
