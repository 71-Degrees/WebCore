/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Deploy;

/// <summary>
/// What a failed gate stage prints: the lines of its output that say why. A refused push used to read "a check could not run" with the reason filtered away; so when no marker line is found,
/// the last lines of the output are shown instead, and a failed stage is never silent.
/// </summary>
internal static class GateOutput
{
    private static readonly string[] Markers = ["[FAIL]", "error", "Failed!", "needs a look", "could not run", "did not complete", "    | "];

    internal static IReadOnlyList<string> Why(string output, int most = 30, int tail = 15)
    {
        var lines = output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        var marked = lines.Where(l => Markers.Any(m => l.Contains(m, StringComparison.Ordinal))).Take(most).ToList();
        return marked.Count > 0 ? marked : lines.TakeLast(tail).ToList();
    }
}
