/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Diagnostics;
using System.Text.Json;

namespace WebCore.Deploy;

/// <summary>What one stage of the check wrote while it ran, kept apart so stages running side by side never interleave: it is printed in stage order once they have all finished.</summary>
internal sealed class StageLog
{
    private readonly List<(string Text, bool IsError)> _lines = [];

    public StageLog(string stage) => Stage = stage;

    public string Stage { get; }

    public TimeSpan Took { get; internal set; }

    public bool Passed { get; internal set; }

    public void WriteLine(string text)
    {
        lock (_lines)
        {
            _lines.Add((text, false));
        }
    }

    public void Error(string text)
    {
        lock (_lines)
        {
            _lines.Add((text, true));
        }
    }

    public IReadOnlyList<(string Text, bool IsError)> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }
}

/// <summary>
/// How long each stage of the pre-push check took, printed as a table when the check ends and appended to <c>reports/check-timings.jsonl</c> (never rewritten), so a push that got slow is a number to look at,
/// not a feeling - and the stages worth moving off the push are the ones at the top of the table. Stages can run side by side: each gets its own <see cref="StageLog"/>.
/// </summary>
internal sealed class CheckTimings
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly List<StageLog> _stages = [];
    private readonly Stopwatch _total = Stopwatch.StartNew();

    /// <summary>Registers a stage (in the order its output will be printed) and returns the log it writes to.</summary>
    public StageLog Begin(string stage)
    {
        var log = new StageLog(stage);
        lock (_stages)
        {
            _stages.Add(log);
        }

        return log;
    }

    /// <summary>Runs a stage's work, timing it and recording whether it passed.</summary>
    public static bool Run(StageLog log, Func<bool> work)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            log.Passed = work();
            return log.Passed;
        }
        finally
        {
            log.Took = clock.Elapsed;
        }
    }

    /// <summary>Prints what each stage wrote, in the order the stages were registered; errors go to the error stream.</summary>
    public void PrintLogs(TextWriter output, TextWriter errors)
    {
        foreach (var stage in Stages())
        {
            foreach (var (text, isError) in stage.Lines)
            {
                (isError ? errors : output).WriteLine(text);
            }
        }
    }

    /// <summary>Prints the table, slowest first, with each stage's share of the whole.</summary>
    public void Print(TextWriter output)
    {
        var total = _total.Elapsed;
        output.WriteLine();
        output.WriteLine($"Where the {total.TotalSeconds:N0}s went (stages ran side by side, so the shares add up to more than 100%):");
        foreach (var stage in Stages().OrderByDescending(s => s.Took))
        {
            var share = total.TotalSeconds <= 0 ? 0 : stage.Took.TotalSeconds / total.TotalSeconds * 100;
            output.WriteLine($"  {stage.Took.TotalSeconds,6:N1}s  {share,3:N0}%  {stage.Stage}{(stage.Passed ? string.Empty : "  (failed)")}");
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
            File.AppendAllLines(path, Stages().Select(s => JsonSerializer.Serialize(new { at, stage = s.Stage, seconds = Math.Round(s.Took.TotalSeconds, 1), passed = s.Passed }, Json)));
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine("  (could not write the timing log: " + ex.Message + ")");
        }
    }

    private List<StageLog> Stages()
    {
        lock (_stages)
        {
            return [.. _stages];
        }
    }
}
