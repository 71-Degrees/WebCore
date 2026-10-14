/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Health;
using WebCore.Storage;

namespace WebCore.Tests.Health;

/// <summary>What a signal says, when it has gone stale, and how a page of them rolls up to one colour.</summary>
public class HealthSignalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static HealthSignal Signal(string id, HealthStatus status, double? everyHours = null, double hoursAgo = 0) =>
        new(id, "Area", id, status, "headline", Now.AddHours(-hoursAgo), ExpectedEveryHours: everyHours);

    [Fact]
    public void ANightlySignal_IsStaleOnlyOnceItIsWellPastDue()
    {
        Assert.False(Signal("a", HealthStatus.Green, 24, hoursAgo: 30).IsStale(Now));
        Assert.True(Signal("a", HealthStatus.Green, 24, hoursAgo: 37).IsStale(Now));
        Assert.False(Signal("live", HealthStatus.Green, everyHours: null, hoursAgo: 900).IsStale(Now));
    }

    [Fact]
    public void AStaleGreenSignal_CountsAsAmber_ButAStaleRedStaysRed()
    {
        Assert.Equal(HealthStatus.Amber, Signal("a", HealthStatus.Green, 24, hoursAgo: 50).EffectiveStatus(Now));
        Assert.Equal(HealthStatus.Red, Signal("a", HealthStatus.Red, 24, hoursAgo: 50).EffectiveStatus(Now));
        Assert.Equal(HealthStatus.Green, Signal("a", HealthStatus.Green, 24, hoursAgo: 2).EffectiveStatus(Now));
    }

    [Fact]
    public void TheRollup_IsTheWorstThingOnThePage()
    {
        var rollup = HealthRollup.Of([Signal("g", HealthStatus.Green), Signal("a", HealthStatus.Amber), Signal("old", HealthStatus.Green, 24, hoursAgo: 60), Signal("r", HealthStatus.Red)], Now);

        Assert.Equal(HealthStatus.Red, rollup.Overall);
        Assert.Equal((1, 2, 1, 1, 4), (rollup.Red, rollup.Amber, rollup.Green, rollup.Stale, rollup.Total));
        Assert.Equal(HealthStatus.Green, HealthRollup.Of([Signal("g", HealthStatus.Green)], Now).Overall);
        Assert.Equal(HealthStatus.Unknown, HealthRollup.Of([], Now).Overall);
    }

    [Fact]
    public void Alerts_AreForRedRecoveryAndSilence_NeverForAmber()
    {
        var red = HealthAlertPolicy.ForTransition("production", new HealthTransition(Signal("q", HealthStatus.Red), HealthStatus.Green));
        var recovered = HealthAlertPolicy.ForTransition("production", new HealthTransition(Signal("q", HealthStatus.Green), HealthStatus.Red));

        Assert.Equal(HealthAlertKind.Red, red!.Kind);
        Assert.Contains("[production]", red.Title, StringComparison.Ordinal);
        Assert.Equal(HealthAlertKind.Recovered, recovered!.Kind);
        Assert.Null(HealthAlertPolicy.ForTransition("production", new HealthTransition(Signal("q", HealthStatus.Amber), HealthStatus.Green)));
        Assert.Null(HealthAlertPolicy.ForTransition("production", new HealthTransition(Signal("q", HealthStatus.Green), HealthStatus.Amber)));
        Assert.Equal(HealthAlertKind.Stale, HealthAlertPolicy.ForStale("staging", Signal("q", HealthStatus.Green, 24, hoursAgo: 40), Now).Kind);
    }

    [Fact]
    public void ASparkline_NeedsTwoPoints_AndDrawsOneLine()
    {
        Assert.Equal(string.Empty, Sparkline.Svg([1.0]));
        Assert.Contains("<polyline", Sparkline.Svg([1.0, 3.0, 2.0]), StringComparison.Ordinal);
    }
}

/// <summary>The store keeps each environment apart, replaces by id, reports what changed level, and bounds its history.</summary>
public sealed class HealthSignalStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wc-health-" + Guid.NewGuid().ToString("N"));
    private readonly HealthSignalStore _store;

    public HealthSignalStoreTests() => _store = new HealthSignalStore(new FileDocumentStore(_folder));

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static HealthSignal Signal(string id, HealthStatus status, int minutes = 0, double? value = null) =>
        new(id, "Quizzes", "Quiz checks", status, "headline", Now.AddMinutes(minutes), Value: value);

    [Fact]
    public async Task EachEnvironment_SeesOnlyItsOwnSignals()
    {
        await _store.PublishAsync("production", [Signal("a", HealthStatus.Green)]);
        await _store.PublishAsync("staging", [Signal("b", HealthStatus.Red)]);

        Assert.Equal(["a"], (await _store.GetAsync("production")).Select(s => s.Id));
        Assert.Equal(["b"], (await _store.GetAsync("staging")).Select(s => s.Id));
    }

    [Fact]
    public async Task Publishing_ReplacesById_AndReportsOnlyWhatChangedLevel()
    {
        Assert.Single(await _store.PublishAsync("production", [Signal("a", HealthStatus.Green)]));

        var same = await _store.PublishAsync("production", [Signal("a", HealthStatus.Green, minutes: 5)]);
        var worse = await _store.PublishAsync("production", [Signal("a", HealthStatus.Red, minutes: 10)]);

        Assert.Empty(same);
        var change = Assert.Single(worse);
        Assert.Equal(HealthStatus.Green, change.Previous);
        Assert.Single(await _store.GetAsync("production"));
    }

    [Fact]
    public async Task TheHistoryKeepsTheNewestPoints_AndIgnoresARepeatOfTheSameMoment()
    {
        for (var i = 0; i < HealthSignalStore.MaxHistoryPerSignal + 10; i++)
        {
            await _store.PublishAsync("production", [Signal("a", HealthStatus.Green, minutes: i, value: i)]);
        }

        await _store.PublishAsync("production", [Signal("a", HealthStatus.Green, minutes: HealthSignalStore.MaxHistoryPerSignal + 9, value: 1)]);

        var points = (await _store.GetHistoryAsync("production"))["a"];
        Assert.Equal(HealthSignalStore.MaxHistoryPerSignal, points.Count);
        Assert.Equal(10, points[0].Value);
    }

    [Fact]
    public void AnEnvironmentName_MustBeSafeInADocumentName()
    {
        Assert.Equal("production", HealthSignalStore.NormalizeEnvironment("Production"));
        Assert.Throws<ArgumentException>(() => HealthSignalStore.NormalizeEnvironment("../etc"));
    }

    [Fact]
    public async Task AReport_IsKeptPerEnvironment_AndAHugeOneIsRefused()
    {
        var reports = new HealthReportStore(new FileBinaryStore(Path.Combine(_folder, "reports")));

        await reports.SaveAsync("production", "quiz-checks", "<html>ok</html>");

        Assert.Equal("<html>ok</html>", await reports.GetAsync("production", "quiz-checks"));
        Assert.Null(await reports.GetAsync("staging", "quiz-checks"));
        await Assert.ThrowsAsync<ArgumentException>(() => reports.SaveAsync("production", "big", new string('x', HealthReportStore.MaxBytes + 1)));
        await Assert.ThrowsAsync<ArgumentException>(() => reports.SaveAsync("production", "../x", "<html/>"));
    }

    [Fact]
    public async Task TheStaleAlertedIds_AreRemembered()
    {
        await _store.SetStaleAlertedAsync("production", new HashSet<string> { "a", "b" });

        Assert.Equal(["a", "b"], (await _store.GetStaleAlertedAsync("production")).Order());
        Assert.Empty(await _store.GetStaleAlertedAsync("staging"));
    }
}
