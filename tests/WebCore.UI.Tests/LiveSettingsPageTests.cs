/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WebCore.LiveSettings;
using WebCore.Storage;
using WebCore.UI;

namespace WebCore.UI.Tests;

public sealed class LiveSettingsPageTests : BunitContext
{
    private static readonly LiveSettingDefinition<int> Limit = LiveSettingFactory.WholeNumber("Ai:CallLimit", 120, 30, 600, "Seconds to wait.", "Ai");
    private static readonly LiveSettingDefinition<bool> Switch = LiveSettingFactory.Toggle("Voice:Commentary", false, "Host remarks.", "Voice");

    private sealed class MemoryStore : IKeyedDocumentStore
    {
        public Dictionary<string, object> Documents { get; } = new();

        public bool FailWrites { get; set; }

        public Task<T?> GetAsync<T>(string name, CancellationToken cancellationToken = default) => Task.FromResult(Documents.TryGetValue(name, out var v) ? (T)v : default);

        public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(Documents.ContainsKey(name));

        public Task SetAsync<T>(string name, T value, CancellationToken cancellationToken = default)
        {
            if (FailWrites)
            {
                throw new IOException("store down");
            }

            Documents[name] = value!;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetIfNotExistsAsync<T>(string name, T value, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(Documents.Remove(name));
    }

    private readonly MemoryStore _store = new();

    private IRenderedComponent<LiveSettingsPage> Open()
    {
        var settings = new LiveSettingsService(_store, new LiveSettingDefinition[] { Limit, Switch }, new LiveSettingsOptions("Production", TimeSpan.FromSeconds(30)));
        Services.AddSingleton<ILiveSettings>(settings);
        Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        return Render<LiveSettingsPage>(p => p.Add(c => c.ChangedBy, "owner"));
    }

    [Fact]
    public void ItShowsEveryRegisteredSettingWithItsDefaultAndRange()
    {
        var page = Open();

        Assert.Contains("Seconds to wait.", page.Find("[data-testid='setting-Ai:CallLimit']").TextContent);
        Assert.Contains("30 to 600", page.Markup);
        Assert.Equal("120", page.Find("[data-testid='input-Ai:CallLimit']").GetAttribute("value"));
        Assert.Contains("Voice", page.Markup);
        Assert.Empty(page.FindAll("[data-testid='reset-Ai:CallLimit']"));
    }

    [Fact]
    public void ItSavesAValidValueAndShowsWhoChangedIt()
    {
        var page = Open();

        page.Find("[data-testid='input-Ai:CallLimit']").Input("90");
        page.Find("[data-testid='save-Ai:CallLimit']").Click();

        page.WaitForAssertion(() => Assert.Contains("Saved.", page.Find("[data-testid='message-Ai:CallLimit']").TextContent));
        Assert.Contains("Changed by owner", page.Find("[data-testid='setting-Ai:CallLimit']").TextContent);
        var stored = (LiveSettingsDocument)_store.Documents["production.json"];
        Assert.Equal("90", stored.Values["Ai:CallLimit"].Value);
        Assert.NotEmpty(page.FindAll("[data-testid='reset-Ai:CallLimit']"));
    }

    [Fact]
    public void ItRefusesAnOutOfRangeValueWithTheReasonAndStoresNothing()
    {
        var page = Open();

        page.Find("[data-testid='input-Ai:CallLimit']").Input("5000");
        page.Find("[data-testid='save-Ai:CallLimit']").Click();

        page.WaitForAssertion(() => Assert.Contains("Must be between 30 and 600", page.Find("[data-testid='message-Ai:CallLimit']").TextContent));
        Assert.Empty(_store.Documents);
    }

    [Fact]
    public void ResetPutsTheDefaultBack()
    {
        var page = Open();
        page.Find("[data-testid='input-Ai:CallLimit']").Input("90");
        page.Find("[data-testid='save-Ai:CallLimit']").Click();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid='reset-Ai:CallLimit']")));

        page.Find("[data-testid='reset-Ai:CallLimit']").Click();

        page.WaitForAssertion(() => Assert.Equal("120", page.Find("[data-testid='input-Ai:CallLimit']").GetAttribute("value")));
        Assert.Contains("Using the default.", page.Find("[data-testid='setting-Ai:CallLimit']").TextContent);
    }

    [Fact]
    public void AToggleIsSavedFromItsDropDown()
    {
        var page = Open();

        page.Find("[data-testid='input-Voice:Commentary']").Change("true");
        page.Find("[data-testid='save-Voice:Commentary']").Click();

        page.WaitForAssertion(() => Assert.Contains("Saved.", page.Find("[data-testid='message-Voice:Commentary']").TextContent));
        var stored = (LiveSettingsDocument)_store.Documents["production.json"];
        Assert.Equal("true", stored.Values["Voice:Commentary"].Value);
    }

    [Fact]
    public void AStoreFailureIsShownNotSwallowed()
    {
        var page = Open();
        _store.FailWrites = true;

        page.Find("[data-testid='input-Ai:CallLimit']").Input("90");
        page.Find("[data-testid='save-Ai:CallLimit']").Click();

        page.WaitForAssertion(() => Assert.Contains("Could not reach the settings store", page.Find("[data-testid='message-Ai:CallLimit']").TextContent));
    }
}
