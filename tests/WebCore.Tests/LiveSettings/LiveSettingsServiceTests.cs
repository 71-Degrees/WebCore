/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.LiveSettings;
using WebCore.Storage;

namespace WebCore.Tests.LiveSettings;

public sealed class LiveSettingsServiceTests
{
    private static readonly LiveSettingDefinition<string> Model = LiveSettingFactory.Text("Ai:WriterModel", "", "The writer's model.", "Ai", allowed: null);
    private static readonly LiveSettingDefinition<int> Limit = LiveSettingFactory.WholeNumber("Ai:WriterCallLimitSeconds", 120, 30, 600, "Seconds.", "Ai");
    private static readonly LiveSettingDefinition<decimal> Price = LiveSettingFactory.Amount("Ai:Price", 0m, 0m, 100m, "Dollars.", "Ai");
    private static readonly LiveSettingDefinition<bool> Switch = LiveSettingFactory.Toggle("Voice:Commentary", false, "On or off.", "Voice");

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class MemoryStore : IKeyedDocumentStore
    {
        public Dictionary<string, object> Documents { get; } = new();

        public int Reads { get; private set; }

        public bool FailReads { get; set; }

        public TimeSpan SlowRead { get; set; }

        public async Task<T?> GetAsync<T>(string name, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (FailReads)
            {
                throw new IOException("store down");
            }

            if (SlowRead > TimeSpan.Zero)
            {
                await Task.Delay(SlowRead, cancellationToken);
            }

            return Documents.TryGetValue(name, out var value) ? (T)value : default;
        }

        public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(Documents.ContainsKey(name));

        public Task SetAsync<T>(string name, T value, CancellationToken cancellationToken = default)
        {
            Documents[name] = value!;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetIfNotExistsAsync<T>(string name, T value, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(Documents.Remove(name));
    }

    private sealed class Recorder : ILiveSettingsAudit
    {
        public List<LiveSettingChange> Changes { get; } = new();

        public bool Fail { get; set; }

        public Task RecordAsync(LiveSettingChange change, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new InvalidOperationException("audit down");
            }

            Changes.Add(change);
            return Task.CompletedTask;
        }
    }

    private static LiveSettingsService Make(MemoryStore store, ManualTime time, List<string>? warnings = null, Recorder? audit = null, string environment = "Production") =>
        new(store, new LiveSettingDefinition[] { Model, Limit, Price, Switch }, new LiveSettingsOptions(environment, TimeSpan.FromSeconds(30), warnings is null ? null : warnings.Add, audit), time);

    [Fact]
    public async Task AMissingDocumentGivesTheDefaults()
    {
        using var settings = Make(new MemoryStore(), new ManualTime());

        Assert.Equal(120, await settings.GetAsync(Limit));
        Assert.Equal(string.Empty, await settings.GetAsync(Model));
        Assert.False(await settings.GetAsync(Switch));
    }

    [Fact]
    public async Task ASavedValueIsReadBackAtOnceAndKeptInTheDocument()
    {
        var store = new MemoryStore();
        using var settings = Make(store, new ManualTime());
        Assert.Equal(120, await settings.GetAsync(Limit));

        var result = await settings.SetAsync(Limit.Key, "90", "owner");

        Assert.True(result.Saved);
        Assert.Equal(90, await settings.GetAsync(Limit));
        var stored = (LiveSettingsDocument)store.Documents["production.json"];
        Assert.Equal("owner", stored.Values[Limit.Key].ChangedBy);
    }

    [Theory]
    [InlineData("29")]
    [InlineData("601")]
    [InlineData("abc")]
    public async Task AnOutOfRangeOrInvalidValueIsRefusedWithAReasonAndNothingIsWritten(string raw)
    {
        var store = new MemoryStore();
        using var settings = Make(store, new ManualTime());

        var result = await settings.SetAsync(Limit.Key, raw, "owner");

        Assert.False(result.Saved);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Empty(store.Documents);
        Assert.Equal(120, await settings.GetAsync(Limit));
    }

    [Fact]
    public async Task AnUnknownKeyIsRefused()
    {
        using var settings = Make(new MemoryStore(), new ManualTime());

        Assert.False((await settings.SetAsync("Nope", "1", "owner")).Saved);
        Assert.False((await settings.ResetAsync("Nope", "owner")).Saved);
    }

    [Fact]
    public async Task AnInvalidStoredValueFallsBackToTheDefaultAndWarnsOnce()
    {
        var store = new MemoryStore();
        store.Documents["production.json"] = new LiveSettingsDocument
        {
            Values = { [Limit.Key] = new LiveSettingEntry("9999", "someone", DateTimeOffset.UnixEpoch) },
        };
        var warnings = new List<string>();
        using var settings = Make(store, new ManualTime(), warnings);

        Assert.Equal(120, await settings.GetAsync(Limit));
        Assert.Equal(120, await settings.GetAsync(Limit));
        Assert.Equal(120, await settings.GetAsync(Limit));

        Assert.Single(warnings);
    }

    [Fact]
    public async Task AStoreThatThrowsGivesDefaultsWarnsOnceAndIsNotCached()
    {
        var store = new MemoryStore { FailReads = true };
        var warnings = new List<string>();
        using var settings = Make(store, new ManualTime(), warnings);

        Assert.Equal(120, await settings.GetAsync(Limit));
        Assert.Equal(120, await settings.GetAsync(Limit));
        Assert.Single(warnings);
        Assert.Equal(2, store.Reads);

        store.FailReads = false;
        store.Documents["production.json"] = new LiveSettingsDocument { Values = { [Limit.Key] = new LiveSettingEntry("45", "o", DateTimeOffset.UnixEpoch) } };
        Assert.Equal(45, await settings.GetAsync(Limit));
    }

    [Fact]
    public async Task ASaveIsRefusedByAStoreThatCannotBeReadRatherThanOverwriting()
    {
        var store = new MemoryStore { FailReads = true };
        using var settings = Make(store, new ManualTime());

        await Assert.ThrowsAsync<IOException>(() => settings.SetAsync(Limit.Key, "90", "owner"));

        Assert.Empty(store.Documents);
    }

    [Fact]
    public async Task AReadIsKeptForTheTimeToLiveThenReadAgain()
    {
        var store = new MemoryStore();
        var time = new ManualTime();
        using var settings = Make(store, time);

        await settings.GetAsync(Limit);
        await settings.GetAsync(Model);
        Assert.Equal(1, store.Reads);

        time.Advance(TimeSpan.FromSeconds(31));
        await settings.GetAsync(Limit);
        Assert.Equal(2, store.Reads);
    }

    [Fact]
    public async Task AChangeMadeElsewhereIsSeenAfterTheTimeToLive()
    {
        var store = new MemoryStore();
        var time = new ManualTime();
        using var settings = Make(store, time);
        Assert.Equal(120, await settings.GetAsync(Limit));

        store.Documents["production.json"] = new LiveSettingsDocument { Values = { [Limit.Key] = new LiveSettingEntry("45", "other server", DateTimeOffset.UnixEpoch) } };
        Assert.Equal(120, await settings.GetAsync(Limit));

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(45, await settings.GetAsync(Limit));
    }

    [Fact]
    public async Task TwoOverlappingSavesOfDifferentKeysBothLand()
    {
        var store = new MemoryStore { SlowRead = TimeSpan.FromMilliseconds(40) };
        using var settings = Make(store, new ManualTime());

        var results = await Task.WhenAll(settings.SetAsync(Limit.Key, "90", "a"), settings.SetAsync(Switch.Key, "true", "b"), settings.SetAsync(Model.Key, "claude-x", "c"));

        Assert.All(results, r => Assert.True(r.Saved));
        store.SlowRead = TimeSpan.Zero;
        Assert.Equal(90, await settings.GetAsync(Limit));
        Assert.True(await settings.GetAsync(Switch));
        Assert.Equal("claude-x", await settings.GetAsync(Model));
    }

    [Fact]
    public async Task EachEnvironmentKeepsItsOwnDocument()
    {
        var store = new MemoryStore();
        using var production = Make(store, new ManualTime(), environment: "Production");
        using var staging = Make(store, new ManualTime(), environment: "Staging");

        await production.SetAsync(Limit.Key, "90", "owner");

        Assert.Equal(90, await production.GetAsync(Limit));
        Assert.Equal(120, await staging.GetAsync(Limit));
        Assert.True(store.Documents.ContainsKey("production.json"));
        Assert.False(store.Documents.ContainsKey("staging.json"));
    }

    [Fact]
    public async Task ResetRemovesTheOverrideAndBothChangesAreAudited()
    {
        var audit = new Recorder();
        using var settings = Make(new MemoryStore(), new ManualTime(), audit: audit);

        await settings.SetAsync(Limit.Key, "90", "owner");
        await settings.ResetAsync(Limit.Key, "owner");

        Assert.Equal(120, await settings.GetAsync(Limit));
        Assert.Equal(2, audit.Changes.Count);
        Assert.Equal(("120", "90"), (audit.Changes[0].OldText, audit.Changes[0].NewText));
        Assert.Equal(("90", null), (audit.Changes[1].OldText, audit.Changes[1].NewText));
    }

    [Fact]
    public async Task AFailingAuditLogDoesNotTurnAStoredChangeIntoAnError()
    {
        var warnings = new List<string>();
        using var settings = Make(new MemoryStore(), new ManualTime(), warnings, new Recorder { Fail = true });

        var result = await settings.SetAsync(Limit.Key, "90", "owner");

        Assert.True(result.Saved);
        Assert.Equal(90, await settings.GetAsync(Limit));
        Assert.Single(warnings);
    }

    [Fact]
    public async Task ListShowsEveryDefinitionWithItsValueInForce()
    {
        using var settings = Make(new MemoryStore(), new ManualTime());
        await settings.SetAsync(Switch.Key, "TRUE", "owner");

        var views = await settings.ListAsync();

        Assert.Equal(4, views.Count);
        var commentary = views.Single(v => v.Definition.Key == Switch.Key);
        Assert.Equal("true", commentary.EffectiveText);
        Assert.Equal("owner", commentary.Override?.ChangedBy);
        Assert.Null(views.Single(v => v.Definition.Key == Limit.Key).Override);
    }

    [Fact]
    public void ADefaultThatBreaksItsOwnRuleFailsWhenBuilt()
    {
        Assert.Throws<ArgumentException>(() => LiveSettingFactory.WholeNumber("X", 5, 10, 20, "d", "g"));
    }

    [Fact]
    public async Task TextCanBeLimitedToAllowedChoices()
    {
        var pick = LiveSettingFactory.Text("Pick", "a", "d", "g", allowed: new[] { "a", "b" });
        using var settings = new LiveSettingsService(new MemoryStore(), new LiveSettingDefinition[] { pick }, new LiveSettingsOptions("staging", TimeSpan.FromSeconds(30)));

        Assert.False((await settings.SetAsync("Pick", "c", "o")).Saved);
        Assert.True((await settings.SetAsync("Pick", " b ", "o")).Saved);
        Assert.Equal("b", await settings.GetAsync(pick));
    }
}
