/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
namespace WebCore.Hosting.Edge;

/// <summary>
/// A Content-Security-Policy built from a strict start: only this site's own scripts, styles, fonts, images (and data: images) and connections; nothing may frame
/// the site; forms post only here. An app adds the exact sources it needs per directive (<see cref="Allow"/>), each with its reason in a comment where it is called,
/// so a loosened directive is never a silent edit of one long string.
/// </summary>
public sealed class ContentSecurityPolicy
{
    private readonly Dictionary<string, List<string>> _directives = new(StringComparer.Ordinal)
    {
        ["default-src"] = ["'self'"],
        ["script-src"] = ["'self'"],
        ["style-src"] = ["'self'"],
        ["img-src"] = ["'self'", "data:"],
        ["font-src"] = ["'self'"],
        ["connect-src"] = ["'self'"],
        ["frame-ancestors"] = ["'none'"],
        ["base-uri"] = ["'self'"],
        ["form-action"] = ["'self'"],
    };

    /// <summary>Adds sources to a directive (creating it when the strict start has none, such as <c>frame-src</c> or <c>media-src</c>). Duplicates are ignored.</summary>
    public ContentSecurityPolicy Allow(string directive, params string[] sources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directive);
        var valid = ValidSources(sources);
        if (!_directives.TryGetValue(directive, out var list))
        {
            list = [];
            _directives[directive] = list;
        }

        foreach (var source in valid.Where(s => !list.Contains(s, StringComparer.Ordinal)))
        {
            list.Add(source);
        }

        return this;
    }

    /// <summary>Replaces a directive's sources outright (for example <c>frame-ancestors 'self'</c> on a page the site frames itself).</summary>
    public ContentSecurityPolicy Set(string directive, params string[] sources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directive);
        _directives[directive] = [.. ValidSources(sources)];
        return this;
    }

    // The policy is written as one header value, so a source holding a separator or a control character could end its
    // directive early or start another one. It is refused where it is configured, not written into the header.
    private static string[] ValidSources(string[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source) || source.Any(IsForbiddenInSource))
            {
                throw new ArgumentException($"A Content-Security-Policy source cannot be empty or hold whitespace, ';', ',' or a control character: \"{source}\".", nameof(sources));
            }
        }

        return sources;
    }

    private static bool IsForbiddenInSource(char c) => c is ';' or ',' || char.IsWhiteSpace(c) || char.IsControl(c);

    /// <summary>A copy to change for one request without touching the site-wide policy.</summary>
    public ContentSecurityPolicy Clone()
    {
        var copy = new ContentSecurityPolicy();
        copy._directives.Clear();
        foreach (var (name, sources) in _directives)
        {
            copy._directives[name] = [.. sources];
        }

        return copy;
    }

    public override string ToString() => string.Join("; ", _directives.Select(d => d.Key + " " + string.Join(' ', d.Value)));
}
