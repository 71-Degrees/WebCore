/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Text.RegularExpressions;

namespace WebCore.Storage;

/// <summary>
/// The one rule for turning an id into a document name. An id that could escape the folder or container (separators, dots) is refused up front, so no store ever sees one.
/// </summary>
public static partial class DocumentNames
{
    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex ValidIdPattern();

    public static bool IsValid(string? id) => !string.IsNullOrWhiteSpace(id) && ValidIdPattern().IsMatch(id);

    /// <summary>The document name for <paramref name="id"/>; throws when the id is not valid. <paramref name="label"/> names the kind of id in the message ("quiz", "user").</summary>
    public static string For(string id, string label) =>
        IsValid(id) ? id + ".json" : throw new ArgumentException($"'{id}' is not a valid {label} id.", nameof(id));
}
