/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebCore.Metadata;

namespace WebCore.ITunes;

/// <summary>The one registration for iTunes: one client per process (its call spacing is shared by all callers).</summary>
public static class ITunesServices
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    public static IServiceCollection AddITunesCatalog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(sp => new ITunesCatalogClient(
            new HttpClient { Timeout = CallTimeout },
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ITunesCatalogClient>>()));
        // Registered before Spotify: the apps list the metadata sources in registration order.
        services.AddSingleton<IAlbumMetadataSource>(sp => sp.GetRequiredService<ITunesCatalogClient>());
        return services;
    }
}
