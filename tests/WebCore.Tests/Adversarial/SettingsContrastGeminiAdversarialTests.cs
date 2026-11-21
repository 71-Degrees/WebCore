/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using System.Text.Json;
using WebCore.Accessibility;
using WebCore.Ai;
using WebCore.LiveSettings;
using WebCore.Storage;

namespace WebCore.Tests.Adversarial;

// Live settings (definition rules, parsing, store failures), the contrast checker, and the Gemini reply parser,
// with nulls, bad bounds, unreadable stores and hostile text.
public sealed class SettingsContrastGeminiAdversarialTests
{
    // ---- Definition rules ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void LiveSetting_BlankKey_IsRefused(string key)
    {
        Assert.Throws<ArgumentException>(() => LiveSettingFactory.Text(key, "x", "d", "g"));
    }

    [Fact]
    public void LiveSetting_KeyOfExactly100Characters_IsAcceptedAndOneMoreIsNot()
    {
        Assert.NotNull(LiveSettingFactory.Text(new string('k', 100), "x", "d", "g"));
        Assert.Throws<ArgumentException>(() => LiveSettingFactory.Text(new string('k', 101), "x", "d", "g"));
    }

    [Fact]
    public void LiveSetting_TextDefaultOverTheLengthRule_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => LiveSettingFactory.Text("k", "toolong", "d", "g", maxLength: 3));
    }

    [Fact]
    public void LiveSetting_EmptyTextDefault_IsAllowedEvenWhenTheListExcludesIt()
    {
        var setting = LiveSettingFactory.Text("k", string.Empty, "d", "g", allowed: ["a", "b"]);

        Assert.Equal(string.Empty, setting.Default);
    }

    [Fact]
    public void LiveSetting_NonEmptyDefaultNotInTheAllowedList_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => LiveSettingFactory.Text("k", "c", "d", "g", allowed: ["a", "b"]));
    }

    [Fact]
    public void LiveSetting_WholeNumberWithMinAboveMax_RefusesItsOwnDefault()
    {
        Assert.Throws<ArgumentException>(() => LiveSettingFactory.WholeNumber("n", 5, minimum: 10, maximum: 1, "d", "g"));
    }

    [Fact]
    public void LiveSetting_WholeNumberDefaultOutsideItsRange_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => LiveSettingFactory.WholeNumber("n", 99, minimum: 1, maximum: 10, "d", "g"));
    }

    [Theory]
    [InlineData("  +5  ", true)]
    [InlineData("5.0", false)]
    [InlineData("2147483648", false)]
    [InlineData("", false)]
    [InlineData("1,000", false)]
    [InlineData("0x10", false)]
    public void LiveSetting_WholeNumberParsing_IsStrictAboutFormat(string raw, bool valid)
    {
        var setting = LiveSettingFactory.WholeNumber("n", 5, int.MinValue, int.MaxValue, "d", "g");

        Assert.Equal(valid, setting.Validate(raw) is null);
    }

    [Theory]
    [InlineData("1,000.50", true)]
    [InlineData("1e3", false)]
    [InlineData("NaN", false)]
    [InlineData("-0", true)]
    [InlineData("79228162514264337593543950336", false)]
    public void LiveSetting_AmountParsing_RefusesExponentsAndNonNumbers(string raw, bool valid)
    {
        var setting = LiveSettingFactory.Amount("a", 1m, -100m, 100_000m, "d", "g");

        Assert.Equal(valid, setting.Validate(raw) is null);
    }

    [Theory]
    [InlineData("TRUE", true)]
    [InlineData(" false ", true)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    public void LiveSetting_ToggleParsing_OnlyTrueOrFalse(string raw, bool valid)
    {
        var setting = LiveSettingFactory.Toggle("t", false, "d", "g");

        Assert.Equal(valid, setting.Validate(raw) is null);
    }

    [Fact]
    public void LiveSetting_TryReadOfABadValue_ReturnsTheDefault()
    {
        var setting = LiveSettingFactory.WholeNumber("n", 7, 1, 10, "d", "g");

        Assert.False(setting.TryRead("junk", out var value));
        Assert.Equal(7, value);
    }

    // ---- LiveSettingsService ----

    [Fact]
    public void LiveSettingsService_DuplicateKeys_AreRefused()
    {
        var a = LiveSettingFactory.Toggle("same", false, "d", "g");
        var b = LiveSettingFactory.Toggle("same", true, "d", "g");

        Assert.Throws<ArgumentException>(() => NewService(new FakeStore(), [a, b]));
    }

    [Theory]
    [InlineData("Prod/../x")]
    [InlineData("")]
    public void LiveSettingsService_BadEnvironmentName_IsRefused(string environment)
    {
        Assert.Throws<ArgumentException>(() => NewService(new FakeStore(), [], environment));
    }

    [Fact]
    public async Task LiveSettingsService_SettingAnUnknownKey_IsRefusedWithAReason()
    {
        var service = NewService(new FakeStore(), []);

        var result = await service.SetAsync("nope", "x", "owner");

        Assert.False(result.Saved);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public async Task LiveSettingsService_NullRawValue_ThrowsNullReference()
    {
        var toggle = LiveSettingFactory.Toggle("t", false, "d", "g");
        var service = NewService(new FakeStore(), [toggle]);

        await Assert.ThrowsAsync<NullReferenceException>(() => service.SetAsync("t", null!, "owner"));
    }

    [Fact]
    public async Task LiveSettingsService_RefusedValue_LeavesTheStoredDocumentAlone()
    {
        var store = new FakeStore();
        var toggle = LiveSettingFactory.Toggle("t", false, "d", "g");
        var service = NewService(store, [toggle]);

        var result = await service.SetAsync("t", "1", "owner");

        Assert.False(result.Saved);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task LiveSettingsService_ResetOfAnUnsetKey_IsSavedAndUnchanged()
    {
        var toggle = LiveSettingFactory.Toggle("t", true, "d", "g");
        var service = NewService(new FakeStore(), [toggle]);

        var result = await service.ResetAsync("t", "owner");

        Assert.True(result.Saved);
        Assert.True(await service.GetAsync(toggle));
    }

    [Fact]
    public async Task LiveSettingsService_StoredValueThatNoLongerValidates_ServesTheDefaultAndWarnsOnce()
    {
        var warnings = new List<string>();
        var store = new FakeStore();
        var toggle = LiveSettingFactory.Toggle("t", true, "d", "g");
        var doc = new LiveSettingsDocument();
        doc.Values["t"] = new LiveSettingEntry("maybe", "owner", DateTimeOffset.UnixEpoch);
        await store.SetAsync("prod.json", doc);
        var service = NewService(store, [toggle], warn: warnings.Add);

        Assert.True(await service.GetAsync(toggle));
        Assert.True(await service.GetAsync(toggle));

        Assert.Single(warnings);
    }

    [Fact]
    public async Task LiveSettingsService_UnreadableStore_ReadsDefaultsButRefusesToSave()
    {
        var store = new FakeStore { FailReads = true };
        var toggle = LiveSettingFactory.Toggle("t", true, "d", "g");
        var warnings = new List<string>();
        var service = NewService(store, [toggle], warn: warnings.Add);

        Assert.True(await service.GetAsync(toggle));
        await Assert.ThrowsAsync<IOException>(() => service.SetAsync("t", "false", "owner"));
        Assert.Equal(0, store.Writes);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public async Task LiveSettingsService_AuditFailure_KeepsTheSaveAndWarns()
    {
        var warnings = new List<string>();
        var toggle = LiveSettingFactory.Toggle("t", false, "d", "g");
        var service = NewService(new FakeStore(), [toggle], warn: warnings.Add, audit: new ThrowingAudit());

        var result = await service.SetAsync("t", "true", "owner");

        Assert.True(result.Saved);
        Assert.Contains(warnings, w => w.Contains("audit"));
    }

    [Fact]
    public void LiveSettingsService_PeekBeforeAnyRead_ServesTheDefault()
    {
        var toggle = LiveSettingFactory.Toggle("t", true, "d", "g");
        var service = NewService(new FakeStore(), [toggle]);

        Assert.True(service.Peek(toggle));
    }

    [Fact]
    public async Task LiveSettingsService_ListingReturnsEverySettingOrderedByGroupThenKey()
    {
        var b = LiveSettingFactory.Toggle("b", false, "d", "alpha");
        var a = LiveSettingFactory.Toggle("a", false, "d", "beta");
        var service = NewService(new FakeStore(), [a, b]);

        var list = await service.ListAsync();

        Assert.Equal(["b", "a"], list.Select(v => v.Definition.Key));
    }

    // ---- CssContrast ----

    [Fact]
    public void CssContrast_NullSheet_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => CssContrast.FindFailures(null!));
    }

    [Fact]
    public void CssContrast_EmptySheet_HasNoFailures()
    {
        Assert.Empty(CssContrast.FindFailures(string.Empty));
    }

    [Fact]
    public void CssContrast_GreyOnWhiteJustBelowAA_IsReported()
    {
        const string css = ":root { --fg: #777; --bg: #fff; } body { color: var(--fg); background-color: var(--bg); }";

        var failures = CssContrast.FindFailures(css);

        Assert.Contains(failures, f => f.Theme == "light" && f.Selector == "body");
    }

    [Fact]
    public void CssContrast_NaNMinimum_ReportsNothing()
    {
        const string css = "body { color: #777; background-color: #fff; }";

        Assert.Empty(CssContrast.FindFailures(css, double.NaN));
    }

    [Fact]
    public void CssContrast_UndefinedVariableWithNoFallback_IsSilentlySkipped_Finding()
    {
        // An unresolved var() drops the pair, so a real contrast problem hidden behind a typo is never reported.
        const string css = "body { color: var(--typo); background-color: #000; }";

        Assert.Empty(CssContrast.FindFailures(css));
    }

    [Fact]
    public void CssContrast_TranslucentColour_HasNoSingleRatioAndIsSkipped()
    {
        const string css = "body { color: rgba(0, 0, 0, 0.5); background-color: #fff; }";

        Assert.Empty(CssContrast.FindFailures(css));
    }

    [Fact]
    public void CssContrast_RgbChannelsAboveTwoFiftyFiveAreClamped()
    {
        const string css = "body { color: rgb(300, 0, 0); background-color: #fff; }";

        var failures = CssContrast.FindFailures(css, 10);

        Assert.NotEmpty(failures);
        Assert.All(failures, f => Assert.Equal("#ff0000", f.Foreground));
    }

    [Fact]
    public void CssContrast_IgnoredSelectorsAreSkipped()
    {
        const string css = ".legal { color: #777; background-color: #fff; }";

        Assert.Empty(CssContrast.FindFailures(css, ignoreSelectors: [".legal"]));
    }

    [Fact]
    public void CssContrast_Ratio_OfBlackAndWhiteIsTwentyOne_AndSymmetric()
    {
        Assert.Equal(21.0, CssContrast.Ratio("#000000", "#ffffff"), 6);
        Assert.Equal(CssContrast.Ratio("#123456", "#abcdef"), CssContrast.Ratio("#abcdef", "#123456"), 9);
    }

    [Fact]
    public void CssContrast_Ratio_OfAColourWithItself_IsOne()
    {
        Assert.Equal(1.0, CssContrast.Ratio("#808080", "#808080"), 6);
    }

    [Fact(Skip = "FINDING F-C1: the public Ratio method only accepts six-digit hex; #fff throws (exception type not checked)")]
    public void CssContrast_Ratio_ThreeDigitHex_IsAccepted_Finding()
    {
        Assert.Equal(21.0, CssContrast.Ratio("#fff", "#000"), 6);
    }

    // ---- GeminiApi parsing ----

    [Fact]
    public void GeminiApi_UsageOfMalformedJson_IsAJsonException()
    {
        Assert.ThrowsAny<JsonException>(() => GeminiApi.Usage("not json"));
    }

    [Fact]
    public void GeminiApi_UsageWithoutMetadata_IsZero()
    {
        Assert.Equal((0L, 0L), GeminiApi.Usage("{}"));
    }

    [Fact]
    public void GeminiApi_UsageWithAStringTokenCount_ThrowsInvalidOperation_AsRecorded()
    {
        Assert.Throws<InvalidOperationException>(() => GeminiApi.Usage("{\"usageMetadata\":{\"promptTokenCount\":\"5\"}}"));
    }

    [Fact]
    public void GeminiApi_ReplyTextOfEmptyObject_IsEmpty()
    {
        Assert.Equal(string.Empty, GeminiApi.ReplyText("{}"));
    }

    [Fact]
    public void GeminiApi_ReplyTextWithCandidatesAsAnObject_ThrowsInvalidOperation_AsRecorded()
    {
        Assert.Throws<InvalidOperationException>(() => GeminiApi.ReplyText("{\"candidates\":{}}"));
    }

    [Fact]
    public void GeminiApi_ReplyTextWithANullPart_JoinsTheRest()
    {
        const string json = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"a\"},{},{\"text\":\"b\"}]}}]}";

        Assert.Equal("ab", GeminiApi.ReplyText(json));
    }

    [Fact]
    public void GeminiApi_ArrayIn_EmptyAndPlainArrays()
    {
        Assert.Empty(GeminiApi.ArrayIn("[]"));
        Assert.Equal(2, GeminiApi.ArrayIn("[1,2]").Count);
        Assert.Empty(GeminiApi.ArrayIn("no brackets here"));
    }

    [Fact]
    public void GeminiApi_ArrayIn_NullText_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => GeminiApi.ArrayIn(null!));
    }

    [Fact(Skip = "FINDING F-G1: the greedy array match runs from the first '[' to the last ']', so stray brackets in a reply hide a valid array")]
    public void GeminiApi_ArrayIn_ReplyWithStrayBrackets_StillFindsTheArray_Finding()
    {
        var items = GeminiApi.ArrayIn("Here is [note 1] then the answer [{\"a\":1}] and [end]");

        Assert.Single(items);
    }

    [Fact]
    public void GeminiApi_ArrayIn_ReplyWithStrayBrackets_ReturnsNothing_AsRecorded()
    {
        Assert.Empty(GeminiApi.ArrayIn("Here is [note 1] then the answer [{\"a\":1}] and [end]"));
    }

    // ---- helpers ----

    private static LiveSettingsService NewService(
        IKeyedDocumentStore store,
        IEnumerable<LiveSettingDefinition> definitions,
        string environment = "prod",
        Action<string>? warn = null,
        ILiveSettingsAudit? audit = null)
    {
        return new LiveSettingsService(store, definitions, new LiveSettingsOptions(environment, TimeSpan.FromMinutes(5), warn, audit));
    }

    private sealed class FakeStore : IKeyedDocumentStore
    {
        private readonly Dictionary<string, object> _docs = new(StringComparer.Ordinal);

        public bool FailReads { get; init; }

        public int Writes { get; private set; }

        public Task<T?> GetAsync<T>(string name, CancellationToken cancellationToken = default)
        {
            if (FailReads)
            {
                throw new IOException("store unreachable");
            }

            return Task.FromResult(_docs.TryGetValue(name, out var value) ? (T)value : default);
        }

        public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(_docs.ContainsKey(name));

        public Task SetAsync<T>(string name, T value, CancellationToken cancellationToken = default)
        {
            Writes++;
            _docs[name] = value!;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetIfNotExistsAsync<T>(string name, T value, CancellationToken cancellationToken = default)
        {
            Writes++;
            return Task.FromResult(_docs.TryAdd(name, value!));
        }

        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(_docs.Remove(name));
    }

    private sealed class ThrowingAudit : ILiveSettingsAudit
    {
        public Task RecordAsync(LiveSettingChange change, CancellationToken cancellationToken = default) => throw new InvalidOperationException("audit down");
    }
}
