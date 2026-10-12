/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Diagnostics;
using System.Text.Json;

namespace WebCore.Deploy;

/// <summary>
/// How long each stage of the pre-push check took, printed as a table when the check ends and appended to <c>reports/check-timings.jsonl</c> (never rewritten), so a push that got slow is a number to look at,
/// not a feeling - and the stages worth moving off the push are the ones at the top of the table.
/// </summary>
internal sealed class CheckTimings
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly List<(string Stage, TimeSpan Took, bool Passed)> _stages = [];
    private readonly Stopwatch _total = Stopwatch.StartNew();

    /// <summary>Runs a stage and records how long it took.</summary>
    public bool Measure(string stage, Func<bool> run)
    {
        var clock = Stopwatch.StartNew();
        var passed = false;
        try
        {
            passed = run();
            return passed;
        }
        finally
        {
            _stages.Add((stage, clock.Elapsed, passed));
        }
    }

    /// <summary>Prints the table, slowest first, with each stage's share of the whole.</summary>
    public void Print(TextWriter output)
    {
        var total = _total.Elapsed;
        output.WriteLine();
        output.WriteLine($"Where the {total.TotalSeconds:N0}s went:");
        foreach (var (stage, took, passed) in _stages.OrderByDescending(s => s.Took))
        {
            var share = total.TotalSeconds <= 0 ? 0 : took.TotalSeconds / total.TotalSeconds * 100;
            output.WriteLine($"  {took.TotalSeconds,6:N1}s  {share,3:N0}%  {stage}{(passed ? string.Empty : "  (failed)")}");
        }
    }

    /// <summary>Appends one line per stage (when, stage, seconds) to the log in the reports folder.</summary>
    public void Append(string root)
    {
        try
        {
            var path = Path.Combine(root, "reports", "check-timings.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var at = DateTimeOffset.Now;
            File.AppendAllLines(path, _stages.Select(s => JsonSerializer.Serialize(new { at, stage = s.Stage, seconds = Math.Round(s.Took.TotalSeconds, 1), passed = s.Passed }, Json)));
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine("  (could not write the timing log: " + ex.Message + ")");
        }
    }
}
