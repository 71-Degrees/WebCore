/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using WebCore.Health;
using WebCore.Storage;

namespace WebCore.Tests.Adversarial;

// Health signals, rollups, alert policy, the signal and report stores, and the sparkline, with absurd numbers,
// bad names, oversized reports and hostile markup.
public sealed class HealthAdversarialTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);
    private readonly TempFolder _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- HealthSignal and rollup ----

    [Fact]
    public void HealthSignal_StalenessWithNaNOrNonPositiveExpectation_IsNeverStale()
    {
        var now = Now;
        Assert.False(Signal(expectedEveryHours: double.NaN).IsStale(now.AddYears(5)));
        Assert.False(Signal(expectedEveryHours: 0).IsStale(now.AddYears(5)));
        Assert.False(Signal(expectedEveryHours: -4).IsStale(now.AddYears(5)));
    }

    [Fact]
    public void HealthSignal_EffectiveStatus_OnlyPromotesQuietGreenOrUnknownToAmber()
    {
        var quietAt = Now.AddHours(-100);

        Assert.Equal(HealthStatus.Amber, Signal(HealthStatus.Green, quietAt, 1).EffectiveStatus(Now));
        Assert.Equal(HealthStatus.Amber, Signal(HealthStatus.Unknown, quietAt, 1).EffectiveStatus(Now));
        Assert.Equal(HealthStatus.Red, Signal(HealthStatus.Red, quietAt, 1).EffectiveStatus(Now));
        Assert.Equal(HealthStatus.Amber, Signal(HealthStatus.Amber, quietAt, 1).EffectiveStatus(Now));
    }

    [Fact]
    public void HealthSignal_ExpectationOfPositiveInfinity_ThrowsOverflow_Finding()
    {
        // The publish endpoint takes this number from the wire; TimeSpan.FromHours rejects infinity, so IsStale throws.
        var signal = Signal(expectedEveryHours: double.PositiveInfinity);

        Assert.Throws<OverflowException>(() => signal.IsStale(Now));
    }

    [Fact(Skip = "FINDING F-H1: an absurd ExpectedEveryHours should be refused at publish, not throw when judged")]
    public void HealthSignal_HugeExpectation_DoesNotThrowWhenJudged_Finding()
    {
        var signal = Signal(expectedEveryHours: 1e300);

        Assert.Null(Record.Exception(() => signal.IsStale(Now)));
    }

    [Fact]
    public void HealthRollup_OfNothing_IsUnknownWithZeroTotal()
    {
        var rollup = HealthRollup.Of(Array.Empty<HealthSignal>(), Now);

        Assert.Equal(HealthStatus.Unknown, rollup.Overall);
        Assert.Equal(0, rollup.Total);
    }

    [Fact]
    public void HealthRollup_ANullSignalInTheList_ThrowsNullReference()
    {
        Assert.Throws<NullReferenceException>(() => HealthRollup.Of([null!], Now));
    }

    [Theory]
    [InlineData(HealthStatus.Red, HealthStatus.Green, HealthStatus.Red)]
    [InlineData(HealthStatus.Amber, HealthStatus.Green, HealthStatus.Amber)]
    [InlineData(HealthStatus.Unknown, HealthStatus.Unknown, HealthStatus.Unknown)]
    public void HealthRollup_WorstStatusWins(HealthStatus a, HealthStatus b, HealthStatus expected)
    {
        var overall = HealthRollup.Of([Signal(a), Signal(b)], Now).Overall;

        Assert.Equal(expected, overall);
    }

    [Theory]
    [InlineData(HealthStatus.Unknown, HealthStatus.Green, false)]
    [InlineData(HealthStatus.Green, HealthStatus.Red, true)]
    [InlineData(HealthStatus.Red, HealthStatus.Green, true)]
    [InlineData(HealthStatus.Amber, HealthStatus.Green, false)]
    [InlineData(HealthStatus.Green, HealthStatus.Green, false)]
    [InlineData(HealthStatus.Green, HealthStatus.Unknown, false)]
    public void HealthAlertPolicy_OnlyRedAndRecoveryAlert(HealthStatus previous, HealthStatus current, bool alerts)
    {
        var transition = new HealthTransition(Signal(current), previous);

        Assert.Equal(alerts, HealthAlertPolicy.ForTransition("prod", transition) is not null);
    }

    [Fact]
    public void HealthAlertPolicy_ForStale_WithAnAsOfInTheFuture_DoesNotThrow()
    {
        var alert = HealthAlertPolicy.ForStale("prod", Signal(asOf: Now.AddHours(3)), Now);

        Assert.Contains("h ago", alert.Title);
    }

    // ---- HealthSignalStore ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData(null)]
    public void HealthSignalStore_BadEnvironmentNames_AreRefused(string? environment)
    {
        Assert.Throws<ArgumentException>(() => HealthSignalStore.NormalizeEnvironment(environment));
    }

    [Fact]
    public void HealthSignalStore_EnvironmentNamesAreLowerCased()
    {
        Assert.Equal("prod-eu", HealthSignalStore.NormalizeEnvironment("PROD-EU"));
    }

    [Fact]
    public async Task HealthSignalStore_PublishingTheSameSignalTwice_OnlyTheFirstIsATransition()
    {
        using var store = new HealthSignalStore(new FileDocumentStore(_temp.Path));
        var signal = Signal(HealthStatus.Red);

        var first = await store.PublishAsync("prod", [signal]);
        var second = await store.PublishAsync("prod", [signal]);

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact]
    public async Task HealthSignalStore_NullSignalId_ThrowsArgumentNull()
    {
        using var store = new HealthSignalStore(new FileDocumentStore(_temp.Path));

        await Assert.ThrowsAnyAsync<ArgumentNullException>(() => store.PublishAsync("prod", [Signal(id: null!)]));
    }

    [Fact]
    public async Task HealthSignalStore_HistoryKeepsAtMostNinetyPoints()
    {
        using var store = new HealthSignalStore(new FileDocumentStore(_temp.Path));
        for (var i = 0; i < HealthSignalStore.MaxHistoryPerSignal + 10; i++)
        {
            await store.PublishAsync("prod", [Signal(HealthStatus.Green, Now.AddMinutes(i))]);
        }

        var history = await store.GetHistoryAsync("prod");

        Assert.Equal(HealthSignalStore.MaxHistoryPerSignal, history["sig"].Count);
    }

    [Fact]
    public async Task HealthSignalStore_SameTimestampTwice_IsNotDuplicatedInHistory()
    {
        using var store = new HealthSignalStore(new FileDocumentStore(_temp.Path));
        await store.PublishAsync("prod", [Signal(HealthStatus.Green, Now)]);
        await store.PublishAsync("prod", [Signal(HealthStatus.Red, Now)]);

        Assert.Single((await store.GetHistoryAsync("prod"))["sig"]);
    }

    // ---- HealthReportStore ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("a b")]
    public void HealthReportStore_BadIds_AreInvalid(string? id)
    {
        Assert.False(HealthReportStore.IsValidId(id));
    }

    [Fact]
    public void HealthReportStore_BadIdOnSave_ThrowsSynchronously()
    {
        var store = new HealthReportStore(new FileBinaryStore(_temp.Path));

        Assert.Throws<ArgumentException>(() =>
        {
            _ = store.SaveAsync("prod", "../escape", "<p>x</p>");
        });
    }

    [Fact]
    public async Task HealthReportStore_ExactlyTheLimit_IsKept_OneByteMoreIsRefused()
    {
        var store = new HealthReportStore(new FileBinaryStore(_temp.Path));
        var atLimit = new string('é', HealthReportStore.MaxBytes / 2);

        await store.SaveAsync("prod", "r1", atLimit);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync("prod", "r1", atLimit + "a"));
    }

    [Fact]
    public async Task HealthReportStore_NullHtml_ThrowsArgumentNull()
    {
        var store = new HealthReportStore(new FileBinaryStore(_temp.Path));

        await Assert.ThrowsAnyAsync<ArgumentNullException>(() => store.SaveAsync("prod", "r1", null!));
    }

    [Fact]
    public async Task HealthReportStore_MissingReport_IsNull()
    {
        var store = new HealthReportStore(new FileBinaryStore(_temp.Path));

        Assert.Null(await store.GetAsync("prod", "nothing-here"));
    }

    // ---- Sparkline ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Sparkline_FewerThanTwoPoints_IsEmpty(int count)
    {
        Assert.Equal(string.Empty, Sparkline.Svg(Enumerable.Repeat(1.0, count).ToList()));
    }

    [Fact]
    public void Sparkline_AllEqualValues_DoNotDivideByZero()
    {
        var svg = Sparkline.Svg([5, 5, 5]);

        Assert.DoesNotContain("NaN", svg);
        Assert.DoesNotContain("Infinity", svg);
    }

    [Fact]
    public void Sparkline_InfinityInTheData_EmitsNaNIntoTheMarkup_AsRecorded()
    {
        var svg = Sparkline.Svg([1, double.PositiveInfinity, 3]);

        Assert.Contains("NaN", svg);
    }

    [Fact(Skip = "FINDING F-H2 (markup): the stroke colour is written into the attribute unescaped; untrusted input could break out of it")]
    public void Sparkline_StrokeWithQuotes_CannotBreakOutOfTheAttribute_Finding()
    {
        var svg = Sparkline.Svg([1, 2], stroke: "red\" onload=\"alert(1)");

        Assert.DoesNotContain("onload", svg);
    }

    private static HealthSignal Signal(
        HealthStatus status = HealthStatus.Green,
        DateTimeOffset? asOf = null,
        double? expectedEveryHours = null,
        string? id = "sig")
    {
        return new HealthSignal(id!, "area", "title", status, "headline", asOf ?? Now, ExpectedEveryHours: expectedEveryHours);
    }
}
