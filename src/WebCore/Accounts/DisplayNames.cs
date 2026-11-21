/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Text.RegularExpressions;

namespace WebCore.Accounts;

/// <summary>A display name is just a label an account shows - free to change and
/// not unique (two accounts are told apart by their email).</summary>
public static class DisplayNames
{
    public const int MaxLength = 40;

    /// <summary>Tidies whitespace and trims; empty stays empty.</summary>
    public static string Tidy(string? name) => Regex.Replace((name ?? string.Empty).Trim(), @"\s+", " ");

    // A control character (NUL, DEL, a stray tab-like code) is refused: the name is shown on pages and written into audit entries,
    // where it would break the line or pass for a blank.
    public static bool IsValid(string? name)
    {
        var tidy = Tidy(name);
        return tidy.Length > 0 && tidy.Length <= MaxLength && !tidy.Any(char.IsControl);
    }

    /// <summary>Compares names ignoring case and extra spacing.</summary>
    public static string Normalize(string? name) => Tidy(name).ToLowerInvariant();
}
