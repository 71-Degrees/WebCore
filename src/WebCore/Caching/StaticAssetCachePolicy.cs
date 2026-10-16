/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Caching;

/// <summary>
/// The browser-cache policy for every kind of file the site serves from wwwroot. A stylesheet or script whose address carries a content hash
/// (<c>?v=...</c>, see StaticAssetVersion) can never change under that address, so it is kept for a year; fonts and images change rarely and are kept for a month.
/// A file type with no policy here (and no entry in <see cref="Exempt"/>) fails the guard test, so a new kind of static file cannot ship without a decision.
/// </summary>
public static class StaticAssetCachePolicy
{
    public const string Immutable = "public, max-age=31536000, immutable";
    public const string Month = "public, max-age=2592000";

    private static readonly HashSet<string> HashedTypes = new(StringComparer.OrdinalIgnoreCase) { ".css", ".js" };
    private static readonly HashSet<string> RarelyChangingTypes = new(StringComparer.OrdinalIgnoreCase) { ".woff2", ".woff", ".png", ".jpg", ".jpeg", ".svg", ".ico", ".webp" };

    /// <summary>File types deliberately left with only an ETag (revalidated each time), and why.</summary>
    public static readonly IReadOnlyDictionary<string, string> Exempt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "about.html is served by the /about endpoint, which rewrites the host name per request",
        [".json"] = "manifests and data files: small, and they change with each deploy",
        [".txt"] = "robots.txt style files: tiny and must reflect the current environment",
        [".ogg"] = "spoken clips (a mascot's narration): re-rendered whenever the voice is tuned, so they revalidate by ETag instead of being kept for a month",
        [".xml"] = "BingSiteAuth.xml, the Bing Webmaster Tools ownership token: a few bytes, fetched rarely, and it must keep answering as it is",
    };

    /// <summary>The Cache-Control value for a served file, or null when it keeps only its ETag.</summary>
    public static string? For(string fileName, bool addressCarriesContentHash)
    {
        var extension = Path.GetExtension(fileName);
        if (addressCarriesContentHash && HashedTypes.Contains(extension))
        {
            return Immutable;
        }

        return RarelyChangingTypes.Contains(extension) ? Month : null;
    }

    /// <summary>True when the file type has a policy (hashed or rarely changing) or is a documented exemption.</summary>
    public static bool IsDecided(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return HashedTypes.Contains(extension) || RarelyChangingTypes.Contains(extension) || Exempt.ContainsKey(extension);
    }
}
