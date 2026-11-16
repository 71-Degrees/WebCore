/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Accounts;

/// <summary>
/// Turns a caller-supplied "where to go afterwards" value into a path that is guaranteed to stay on
/// this site, so it can never be used as an open redirect. A bare path like "quizzes" gets its
/// leading slash (Blazor's relative paths omit it); anything that a browser could read as pointing
/// to another host - "//evil.example", "/\evil.example", a scheme, control characters - collapses
/// to "/".
/// </summary>
public static class LocalReturnUrl
{
    public static string Sanitize(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return "/";
        }

        var candidate = returnUrl.Trim();
        if (candidate.Any(char.IsControl) || candidate.StartsWith('\\'))
        {
            return "/";
        }

        if (!candidate.StartsWith('/'))
        {
            candidate = "/" + candidate;
        }

        // "//host" is protocol-relative and "/\host" is treated the same way by browsers.
        if (candidate.Length > 1 && (candidate[1] == '/' || candidate[1] == '\\'))
        {
            return "/";
        }

        return candidate;
    }
}
