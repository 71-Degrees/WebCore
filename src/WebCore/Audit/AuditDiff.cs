/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WebCore.Audit;

/// <summary>
/// What differs between two versions of the same item, field by field, in words a reviewer reads (<c>Questions[2].Text</c>, before and after). One rule for every
/// kind, so a field added later is audited without more code. Top-level fields named in the ignored set (bookkeeping such as <c>UpdatedAt</c>, or bulky data no
/// person reads, such as image fingerprints) are left out.
/// </summary>
public static class AuditDiff
{
    public const int MaxChanges = 40;
    public const int MaxValueLength = 160;

    /// <summary>Left out when no set is given: bookkeeping that changes on every save and says nothing about what was edited.</summary>
    public static readonly IReadOnlySet<string> DefaultIgnored = new HashSet<string>(StringComparer.Ordinal) { "UpdatedAt" };

    public static IReadOnlyList<AuditChange> Compare<T>(T? before, T after, IReadOnlySet<string>? ignored = null)
    {
        var skip = ignored ?? DefaultIgnored;
        var now = Flatten(after, skip);
        var was = before is null ? new Dictionary<string, string?>() : Flatten(before, skip);
        var changes = new List<AuditChange>();
        foreach (var field in was.Keys.Union(now.Keys).Order(StringComparer.Ordinal))
        {
            was.TryGetValue(field, out var b);
            now.TryGetValue(field, out var a);
            if (b != a)
            {
                changes.Add(new AuditChange(field, Shorten(b), Shorten(a)));
            }
        }

        if (changes.Count <= MaxChanges)
        {
            return changes;
        }

        var more = changes.Count - MaxChanges;
        return [.. changes.Take(MaxChanges), new AuditChange($"(and {more} more fields)", null, null)];
    }

    private static Dictionary<string, string?> Flatten<T>(T value, IReadOnlySet<string> ignored)
    {
        var leaves = new Dictionary<string, string?>(StringComparer.Ordinal);
        Walk(JsonSerializer.SerializeToNode(value), string.Empty, leaves, ignored);
        return leaves;
    }

    private static void Walk(JsonNode? node, string path, Dictionary<string, string?> leaves, IReadOnlySet<string> ignored)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    if (path.Length > 0 || !ignored.Contains(name))
                    {
                        Walk(child, path.Length == 0 ? name : $"{path}.{name}", leaves, ignored);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    Walk(array[i], $"{path}[{i}]", leaves, ignored);
                }

                break;
            case null:
                break;   // null and absent read the same
            default:
                leaves[path] = node.ToJsonString().Trim('"');
                break;
        }
    }

    private static string? Shorten(string? value) => value is { Length: > MaxValueLength } ? value[..MaxValueLength] + "…" : value;
}
