/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Metadata;

/// <summary>
/// An outside album catalog the apps fall back to when MusicBrainz is busy (Spotify, iTunes): albums by barcode, artists by name, an artist's albums. Each
/// implementation is registered as this interface, in the order the apps should try them. Never throws for a network or status failure.
/// </summary>
public interface IAlbumMetadataSource
{
    /// <summary>The short lowercase name of the service ("spotify", "itunes"): it prefixes the ids of its albums and names it in the logs.</summary>
    string Name { get; }

    /// <summary>The albums held under this barcode (UPC or EAN). A barcode that is not in the service is NotFound, which is normal.</summary>
    Task<MetadataResult<IReadOnlyList<MetadataAlbum>>> SearchAlbumsByUpcAsync(string upc, CancellationToken cancellationToken = default);

    /// <summary>Artists matching the name, the best match first.</summary>
    Task<MetadataResult<IReadOnlyList<MetadataArtist>>> SearchArtistAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>The artist's studio albums, as far as the service gives them.</summary>
    Task<MetadataResult<IReadOnlyList<MetadataAlbum>>> GetArtistAlbumsAsync(string artistId, CancellationToken cancellationToken = default);
}
