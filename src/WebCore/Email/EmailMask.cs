/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Email;

/// <summary>One place that hides most of an email address, for any place an address is shown or logged.</summary>
public static class EmailMask
{
    /// <summary>"sa***@example.com": the first two letters and the domain, enough to tell two players with the
    /// same display name apart wherever an address is shown publicly, without publishing it.</summary>
    public static string Mask(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0)
        {
            return "***";
        }

        var shown = at <= 2 ? 1 : 2;
        return email[..shown] + "***" + email[at..];
    }
}
