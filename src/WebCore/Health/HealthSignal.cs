/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Text.Json.Serialization;

namespace WebCore.Health;

/// <summary>How a signal reads: green is fine, amber wants a look, red wants action. Unknown is for a signal that has never reported.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HealthStatus
{
    Unknown,
    Green,
    Amber,
    Red,
}

/// <summary>
/// One thing the dashboard asserts, in a shape every tool can publish: the quiz checker, the deploy tool, the code-quality run, the morning check, or the site looking at itself. Small on purpose, so a new tool
/// costs a few lines rather than a new page.
/// </summary>
/// <param name="Id">Stable, short, letters digits and dashes ("quiz-checks"); publishing again with the same id replaces the signal.</param>
/// <param name="Area">The group it sits in on the dashboard ("Quizzes", "Release", "Code").</param>
/// <param name="Headline">The one line a person reads: "142 of 152 quizzes known good".</param>
/// <param name="Value">A number to chart over time, when there is one (milliseconds, a count).</param>
/// <param name="AsOf">When the thing was actually measured (not when it was published).</param>
/// <param name="ExpectedEveryHours">How often it should report. Past one and a half times this it shows as stale rather than showing its old result as if it were fresh. Null for a live signal.</param>
/// <param name="Link">A page on the site that has more ("/admin/quiz-checks").</param>
/// <param name="ReportId">The id of a hosted report (see <see cref="HealthReportStore"/>) that opens the full detail.</param>
public sealed record HealthSignal(
    string Id,
    string Area,
    string Title,
    HealthStatus Status,
    string Headline,
    DateTimeOffset AsOf,
    string? Detail = null,
    double? Value = null,
    string? Unit = null,
    double? ExpectedEveryHours = null,
    string? Link = null,
    string? ReportId = null)
{
    /// <summary>Slack before a signal counts as stale: a nightly job that runs a little late is not an alarm.</summary>
    public const double StaleAfterMultiple = 1.5;

    public bool IsStale(DateTimeOffset now) =>
        ExpectedEveryHours is double hours && hours > 0 && now - AsOf > TimeSpan.FromHours(hours * StaleAfterMultiple);

    /// <summary>What the signal counts as in the roll-up: a stale signal is at least amber, whatever its last result said.</summary>
    public HealthStatus EffectiveStatus(DateTimeOffset now) =>
        IsStale(now) && Status is HealthStatus.Green or HealthStatus.Unknown ? HealthStatus.Amber : Status;
}

/// <summary>One point of a signal's history, for the sparkline.</summary>
public sealed record HealthPoint(DateTimeOffset At, double? Value, HealthStatus Status);

/// <summary>How the whole dashboard reads at a glance: the worst thing on it, and how many things sit at each level.</summary>
public sealed record HealthRollup(HealthStatus Overall, int Red, int Amber, int Green, int Stale, int Total)
{
    public static HealthRollup Of(IEnumerable<HealthSignal> signals, DateTimeOffset now)
    {
        var all = signals.ToList();
        var effective = all.Select(s => s.EffectiveStatus(now)).ToList();
        var red = effective.Count(s => s == HealthStatus.Red);
        var amber = effective.Count(s => s == HealthStatus.Amber);
        var green = effective.Count(s => s == HealthStatus.Green);
        var overall = red > 0 ? HealthStatus.Red : amber > 0 ? HealthStatus.Amber : green > 0 ? HealthStatus.Green : HealthStatus.Unknown;
        return new HealthRollup(overall, red, amber, green, all.Count(s => s.IsStale(now)), all.Count);
    }
}
