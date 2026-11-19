/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebCore.Metadata;

namespace WebCore.Spotify;

/// <summary>The one registration for Spotify: the credentials from configuration, and the catalog client (one per process: its token is shared by all calls).</summary>
public static class SpotifyServices
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    public static IServiceCollection AddSpotifyCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = new SpotifySettings
        {
            ClientId = configuration["Spotify:ClientId"],
            ClientSecret = configuration["Spotify:ClientSecret"],
        };
        services.AddSingleton(settings);
        services.AddSingleton(sp => new SpotifyCatalogClient(
            new HttpClient { Timeout = CallTimeout },
            settings,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<SpotifyCatalogClient>>()));
        // Registered after iTunes: the apps try the metadata sources in registration order.
        services.AddSingleton<IAlbumMetadataSource>(sp => sp.GetRequiredService<SpotifyCatalogClient>());
        return services;
    }
}
