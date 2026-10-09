/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using NUglify;
using NUglify.Css;
using NUglify.JavaScript;

namespace WebCore.Deploy;

/// <summary>
/// Shrinks the site's own stylesheet and scripts in a published copy (never in the source tree): comments and whitespace go, and expressions are
/// tidied. Local variable names are deliberately kept - these are plain scripts that share globals with the Blazor page (a window-level object and the
/// functions it calls), so renaming anything is not worth the few bytes it would save. A file that fails to minify is left exactly as it was.
/// </summary>
internal static class Minifier
{
    private const string Notice = "/* Copyright 2026 71 Degrees. All rights reserved. */\n";

    public static (int Files, long Before, long After, List<string> Skipped) MinifyWebRoot(string webRoot)
    {
        var skipped = new List<string>();
        long before = 0, after = 0;
        var files = 0;
        foreach (var path in Targets(webRoot))
        {
            var source = File.ReadAllText(path);
            var result = Shrink(path, source);
            if (result.HasErrors || string.IsNullOrWhiteSpace(result.Code))
            {
                skipped.Add($"{Path.GetFileName(path)}: {string.Join("; ", result.Errors.Take(2).Select(e => e.Message))}");
                continue;
            }

            var minified = Notice + result.Code;
            if (minified.Length >= source.Length)
            {
                skipped.Add($"{Path.GetFileName(path)}: not smaller");
                continue;
            }

            File.WriteAllText(path, minified);
            RemoveStaleCompressedCopies(path);
            before += source.Length;
            after += minified.Length;
            files++;
        }

        return (files, before, after, skipped);
    }

    /// <summary>The files that get minified: app.css and the scripts under js/.</summary>
    private static List<string> Targets(string webRoot)
    {
        var targets = new List<string>();
        var css = Path.Combine(webRoot, "app.css");
        if (File.Exists(css))
        {
            targets.Add(css);
        }

        var jsDir = Path.Combine(webRoot, "js");
        if (Directory.Exists(jsDir))
        {
            targets.AddRange(Directory.GetFiles(jsDir, "*.js"));
        }

        return targets;
    }

    private static UglifyResult Shrink(string path, string source) =>
        path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
            ? Uglify.Css(source, new CssSettings { CommentMode = CssComment.None })
            : Uglify.Js(source, new CodeSettings
            {
                LocalRenaming = LocalRenaming.KeepAll,
                PreserveFunctionNames = true,
                PreserveImportantComments = false,
                EvalTreatment = EvalTreatment.Ignore,
                OutputMode = OutputMode.SingleLine,
            });

    // A publish precompresses every static file (.br/.gz) from the ORIGINAL text. Nothing here serves those variants, but leaving stale copies of
    // a file we just rewrote is asking for trouble - remove them.
    private static void RemoveStaleCompressedCopies(string path)
    {
        foreach (var stale in new[] { path + ".br", path + ".gz" })
        {
            File.Delete(stale);
        }
    }
}
