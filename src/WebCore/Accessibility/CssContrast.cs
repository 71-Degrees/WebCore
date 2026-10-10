/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Globalization;
using System.Text.RegularExpressions;

namespace WebCore.Accessibility;

/// <summary>One rule whose text colour and background are too close (WCAG AA wants 4.5:1 for normal text), in the light theme or the dark one.</summary>
public sealed record ContrastFailure(string Selector, string Theme, string Foreground, string Background, double Ratio);

/// <summary>
/// A static contrast check for a stylesheet, run as a unit test so a colour change that fails WCAG AA cannot be committed. It reads every rule that sets both <c>color</c> and a <c>background</c>, resolves
/// <c>var(--token)</c> against the <c>:root</c> tokens (and the dark overrides inside <c>@media (prefers-color-scheme: dark)</c>), and reports the pairs under the minimum ratio. It only judges what it can resolve
/// (hex, rgb(), tokens; not gradients, images or colours set by script), so it complements the crawler's axe-core run rather than replacing it: the crawler sees rendered pages, this sees every rule, including
/// screens that need a sign-in.
/// </summary>
public static partial class CssContrast
{
    private const string DarkMedia = "@media (prefers-color-scheme: dark)";

    /// <summary>The rules whose resolved text and background colours fall under <paramref name="minimumRatio"/>, once per theme. <paramref name="ignoreSelectors"/> skips rules whose selector contains any of the fragments.</summary>
    public static IReadOnlyList<ContrastFailure> FindFailures(string css, double minimumRatio = 4.5, IEnumerable<string>? ignoreSelectors = null)
    {
        var ignore = ignoreSelectors?.ToList() ?? new List<string>();
        var rules = ReadRules(css);
        var failures = new List<ContrastFailure>();
        foreach (var theme in new[] { "light", "dark" })
        {
            var tokens = Tokens(rules, theme);
            foreach (var rule in rules.Where(r => r.AppliesTo(theme) && !ignore.Any(i => r.Selector.Contains(i, StringComparison.Ordinal))))
            {
                if (Pair(rule, tokens) is { } pair && Ratio(pair.Foreground, pair.Background) is var ratio && ratio < minimumRatio)
                {
                    failures.Add(new ContrastFailure(rule.Selector, theme, pair.Foreground, pair.Background, Math.Round(ratio, 2)));
                }
            }
        }

        return failures;
    }

    /// <summary>The WCAG contrast ratio between two colours given as #rrggbb.</summary>
    public static double Ratio(string foreground, string background)
    {
        var (a, b) = (Luminance(foreground), Luminance(background));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private sealed record Rule(string Selector, string Body, bool InDarkMedia)
    {
        public bool AppliesTo(string theme) => theme == "dark" || !InDarkMedia;
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"([^{}]+)\{([^{}]*)\}")]
    private static partial Regex Block();

    [GeneratedRegex(@"(--[\w-]+)\s*:\s*([^;]+)")]
    private static partial Regex TokenDeclaration();

    [GeneratedRegex(@"(?<![\w-])(color|background-color|background)\s*:\s*([^;]+)")]
    private static partial Regex Declaration();

    [GeneratedRegex(@"var\(\s*(--[\w-]+)\s*(?:,\s*([^)]+))?\)")]
    private static partial Regex VarReference();

    private static List<Rule> ReadRules(string css)
    {
        var clean = Comments().Replace(css, string.Empty);
        var dark = DarkRanges(clean);
        return Block().Matches(clean)
            .Select(m => new Rule(m.Groups[1].Value.Trim(), m.Groups[2].Value, dark.Any(r => m.Index >= r.Start && m.Index < r.End)))
            .Where(r => !r.Selector.StartsWith('@'))
            .ToList();
    }

    /// <summary>The character ranges of the dark-theme media blocks (found by matching braces from the media query).</summary>
    private static List<(int Start, int End)> DarkRanges(string css)
    {
        var ranges = new List<(int, int)>();
        for (var at = css.IndexOf(DarkMedia, StringComparison.Ordinal); at >= 0; at = css.IndexOf(DarkMedia, at + 1, StringComparison.Ordinal))
        {
            var open = css.IndexOf('{', at);
            var depth = 0;
            for (var i = open; i >= 0 && i < css.Length; i++)
            {
                depth += css[i] == '{' ? 1 : css[i] == '}' ? -1 : 0;
                if (depth == 0)
                {
                    ranges.Add((open, i));
                    break;
                }
            }
        }

        return ranges;
    }

    private static Dictionary<string, string> Tokens(List<Rule> rules, string theme)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in rules.Where(r => r.Selector.Contains(":root", StringComparison.Ordinal) && (!r.InDarkMedia || theme == "dark")).OrderBy(r => r.InDarkMedia))
        {
            foreach (Match token in TokenDeclaration().Matches(rule.Body))
            {
                tokens[token.Groups[1].Value] = token.Groups[2].Value.Trim();
            }
        }

        return tokens;
    }

    private static (string Foreground, string Background)? Pair(Rule rule, Dictionary<string, string> tokens)
    {
        string? foreground = null, background = null;
        foreach (Match declaration in Declaration().Matches(rule.Body))
        {
            var colour = Resolve(declaration.Groups[2].Value.Trim(), tokens, 0);
            if (colour is null)
            {
                continue;
            }

            if (declaration.Groups[1].Value == "color")
            {
                foreground = colour;
            }
            else
            {
                background = colour;
            }
        }

        return foreground is not null && background is not null ? (foreground, background) : null;
    }

    /// <summary>A #rrggbb colour for a CSS value, or null when it is not a single plain colour (a gradient, a keyword, a transparent rgba, an unknown token).</summary>
    private static string? Resolve(string value, Dictionary<string, string> tokens, int depth)
    {
        value = value.Replace("!important", string.Empty, StringComparison.Ordinal).Trim();
        if (depth > 6)
        {
            return null;
        }

        if (VarReference().Match(value) is { Success: true } reference && reference.Index == 0 && reference.Length == value.Length)
        {
            var next = tokens.TryGetValue(reference.Groups[1].Value, out var token) ? token : reference.Groups[2].Success ? reference.Groups[2].Value.Trim() : null;
            return next is null ? null : Resolve(next, tokens, depth + 1);
        }

        return Hex(value) ?? Rgb(value);
    }

    private static string? Hex(string value)
    {
        if (!Regex.IsMatch(value, "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$"))
        {
            return null;
        }

        var hex = value[1..];
        return "#" + (hex.Length == 3 ? string.Concat(hex.Select(c => new string(c, 2))) : hex).ToLowerInvariant();
    }

    private static string? Rgb(string value)
    {
        var match = Regex.Match(value, @"^rgba?\(\s*(\d{1,3})\s*[, ]\s*(\d{1,3})\s*[, ]\s*(\d{1,3})\s*(?:[,/]\s*([\d.]+%?)\s*)?\)$");
        if (!match.Success || (match.Groups[4].Success && match.Groups[4].Value != "1" && match.Groups[4].Value != "100%"))
        {
            return null;   // a translucent colour has no single contrast ratio
        }

        return "#" + string.Concat(Enumerable.Range(1, 3).Select(i => Math.Min(255, int.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture)).ToString("x2", CultureInfo.InvariantCulture)));
    }

    private static double Luminance(string hex)
    {
        double Channel(int start)
        {
            var v = int.Parse(hex.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(1)) + (0.7152 * Channel(3)) + (0.0722 * Channel(5));
    }
}
