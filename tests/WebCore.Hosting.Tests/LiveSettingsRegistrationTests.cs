/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebCore.Hosting.LiveSettings;
using WebCore.LiveSettings;
using WebCore.Storage;

namespace WebCore.Hosting.Tests;

public sealed class LiveSettingsRegistrationTests : IDisposable
{
    private static readonly LiveSettingDefinition<int> Limit = LiveSettingFactory.WholeNumber("Ai:CallLimit", 120, 30, 600, "Seconds.", "Ai");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "live-settings-" + Guid.NewGuid().ToString("N"));

    private sealed class CollectingAudit : ILiveSettingsAudit
    {
        public List<LiveSettingChange> Changes { get; } = new();

        public Task RecordAsync(LiveSettingChange change, CancellationToken cancellationToken = default)
        {
            Changes.Add(change);
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    [Fact]
    public async Task TheRegisteredServiceSavesToTheStoreAndTellsTheAppsAuditLog()
    {
        var audit = new CollectingAudit();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ILiveSettingsAudit>(audit);
        services.AddLiveSettings(new LiveSettingsRegistrationOptions(new LiveSettingDefinition[] { Limit }, "Staging", _ => new FileDocumentStore(_folder)));
        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<ILiveSettings>();

        Assert.Same(settings, provider.GetRequiredService<ILiveSettings>());
        Assert.Equal(120, await settings.GetAsync(Limit));
        Assert.True((await settings.SetAsync(Limit.Key, "60", "owner")).Saved);

        Assert.Equal(60, await settings.GetAsync(Limit));
        Assert.True(File.Exists(Path.Combine(_folder, "staging.json")));
        Assert.Equal("owner", Assert.Single(audit.Changes).ChangedBy);
    }
}
