/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.LiveSettings;
using WebCore.Storage;

namespace WebCore.Hosting.LiveSettings;

/// <summary>What an app tells <see cref="LiveSettingsRegistration.AddLiveSettings"/>: its tunables, its environment's name, and where the settings document lives.</summary>
public sealed record LiveSettingsRegistrationOptions(
    IReadOnlyList<LiveSettingDefinition> Definitions,
    string Environment,
    Func<IServiceProvider, IKeyedDocumentStore> Store,
    TimeSpan? CacheTimeToLive = null);

public static class LiveSettingsRegistration
{
    /// <summary>The default time a read of the settings document is kept: a change on another server is seen within it.</summary>
    public static readonly TimeSpan DefaultCacheTimeToLive = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Registers <see cref="ILiveSettings"/> as one service for the whole app. An <see cref="ILiveSettingsAudit"/> registered by the app is picked up when there is one; warnings go to the log.
    /// </summary>
    public static IServiceCollection AddLiveSettings(this IServiceCollection services, LiveSettingsRegistrationOptions registration)
    {
        services.AddSingleton<ILiveSettings>(provider =>
        {
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("WebCore.LiveSettings");
            var options = new LiveSettingsOptions(
                registration.Environment,
                registration.CacheTimeToLive ?? DefaultCacheTimeToLive,
                message => logger.LogWarning("{Message}", message),
                provider.GetService<ILiveSettingsAudit>());
            return new LiveSettingsService(registration.Store(provider), registration.Definitions, options, provider.GetService<TimeProvider>());
        });
        return services;
    }
}
