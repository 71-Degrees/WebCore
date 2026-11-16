/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
namespace WebCore.Accounts;

public enum PasswordProblem
{
    None,
    TooShort,
    TooLong,
    Mismatch,
}

/// <summary>What a new password has to be, in one place: registering, changing, setting and resetting all use it, so they cannot drift apart.</summary>
public static class PasswordRules
{
    public const int MinimumLength = 8;

    // A ceiling so a multi-megabyte "password" cannot be used to make the hasher do pointless work. Only new passwords are held to it: signing in with
    // an older, longer one still works.
    public const int MaximumLength = 128;

    /// <summary>The first thing wrong with the new password, or <see cref="PasswordProblem.None"/>.</summary>
    public static PasswordProblem Check(string? password, string? confirmation)
    {
        var length = password?.Length ?? 0;
        if (length < MinimumLength)
        {
            return PasswordProblem.TooShort;
        }

        if (length > MaximumLength)
        {
            return PasswordProblem.TooLong;
        }

        return password == confirmation ? PasswordProblem.None : PasswordProblem.Mismatch;
    }

    /// <summary>The first thing wrong with the new password in words for the person, or null when it is acceptable.</summary>
    public static string? Validate(string? password, string? confirmation) => Describe(Check(password, confirmation));

    public static string? Describe(PasswordProblem problem) => problem switch
    {
        PasswordProblem.TooShort => $"Password must be at least {MinimumLength} characters.",
        PasswordProblem.TooLong => $"Password can be at most {MaximumLength} characters.",
        PasswordProblem.Mismatch => "Passwords don't match.",
        _ => null,
    };

    /// <summary>The short code a form redirect carries in its query string (read back by <see cref="AccountMessages.ForPasswordError"/>).</summary>
    public static string Code(PasswordProblem problem) => problem switch
    {
        PasswordProblem.TooShort => "short",
        PasswordProblem.TooLong => "long",
        PasswordProblem.Mismatch => "mismatch",
        _ => string.Empty,
    };
}
