/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Globalization;

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

    /// <summary>The first genre the service gives the album (iTunes primary genre, Spotify's first genre); null when it gives none.</summary>
    public string? Genre { get; init; }

    /// <summary>The full release date as yyyy-MM-dd, when the service gives a whole date; null for a year or month alone.</summary>
    public string? ReleaseDate { get; init; }

    /// <summary>How many tracks the album has, when the service says.</summary>
    public int? TrackCount { get; init; }
}

/// <summary>One track of an album as a service lists it: its disc (1 when the service gives none), its place on that disc, its title and length in milliseconds.</summary>
public sealed record MetadataTrack(int DiscNumber, int TrackNumber, string Title, int? LengthMs);

/// <summary>One artist as a search gives it.</summary>
public sealed record MetadataArtist(string Id, string Name);

/// <summary>Release dates as the services write them, reduced to the one form the catalog keeps.</summary>
public static class MetadataDates
{
    /// <summary>The whole date of a release as yyyy-MM-dd, from "1992-05-12" or "1992-05-12T07:00:00Z"; null when only a year or month is given, or none.</summary>
    public static string? FullDate(string? date)
    {
        if (date is not { Length: >= 10 } || !DateOnly.TryParseExact(date.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return null;
        }

        return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
