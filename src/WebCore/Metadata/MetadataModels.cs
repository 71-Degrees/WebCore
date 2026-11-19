/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Metadata;

/// <summary>How a metadata call ended: Ok with data; NotFound (nothing matched); Busy (refused or slow for now, or held back); Failed (anything else).</summary>
public enum MetadataStatus
{
    Ok,
    NotFound,
    Busy,
    Failed,
}

/// <summary>The answer of one call: its status and, when Ok, the parsed data (null otherwise).</summary>
public sealed record MetadataResult<T>(MetadataStatus Status, T? Data = null) where T : class;

/// <summary>One album as a metadata service gives it. <see cref="CoverUrl"/> is a direct image address; <see cref="AlbumType"/> is "album" for a studio album.</summary>
public sealed record MetadataAlbum
{
    /// <summary>The service's own id for the album (not prefixed).</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string ArtistName { get; init; }

    public string? ArtistId { get; init; }

    public int? ReleaseYear { get; init; }

    public required string AlbumType { get; init; }

    public string? CoverUrl { get; init; }

    public string? Upc { get; init; }
}

/// <summary>One artist as a search gives it.</summary>
public sealed record MetadataArtist(string Id, string Name);
