/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WebCore.Metadata;

namespace WebCore.ITunes;

/// <summary>
/// Apple's public iTunes Search and Lookup API: no key, so no credentials. Apple documents about 20 calls a minute per address, so calls are spaced
/// <see cref="Spacing"/> apart here; an HTTP 403, 429 or timeout answers Busy and holds every call for <see cref="BusyHold"/>. Never throws for a network or
/// status failure. Covers come back at 100 px and are asked for at 600 px instead.
/// </summary>
public sealed class ITunesCatalogClient : IAlbumMetadataSource
{
    public static readonly TimeSpan Spacing = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan BusyHold = TimeSpan.FromMinutes(1);

    private const string LookupUrl = "https://itunes.apple.com/lookup?";

    private const string SearchUrl = "https://itunes.apple.com/search?";

    private const string SmallArt = "100x100bb";

    private const string LargeArt = "600x600bb";

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger<ITunesCatalogClient> _logger;
    private readonly object _gate = new();
    private DateTimeOffset _nextCall;
    private DateTimeOffset _heldUntil;

    public ITunesCatalogClient(HttpClient http, TimeProvider time, ILogger<ITunesCatalogClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => "itunes";

    public Task<MetadataResult<IReadOnlyList<MetadataAlbum>>> SearchAlbumsByUpcAsync(string upc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upc);
        return upc.Length > 0 && upc.All(char.IsAsciiDigit)
            ? AlbumsByUpcAsync(upc, cancellationToken)
            : Task.FromResult(new MetadataResult<IReadOnlyList<MetadataAlbum>>(MetadataStatus.NotFound));
    }

    // A lookup by UPC answers the album with that UPC, so the barcode is the album's UPC as iTunes gives it.
    private async Task<MetadataResult<IReadOnlyList<MetadataAlbum>>> AlbumsByUpcAsync(string upc, CancellationToken cancellationToken)
    {
        var result = await AlbumsAsync(LookupUrl + "upc=" + upc + "&entity=album", cancellationToken).ConfigureAwait(false);
        return result.Status != MetadataStatus.Ok
            ? result
            : new MetadataResult<IReadOnlyList<MetadataAlbum>>(MetadataStatus.Ok, result.Data!.Select(album => album with { Upc = upc }).ToList());
    }

    public async Task<MetadataResult<IReadOnlyList<MetadataArtist>>> SearchArtistAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var result = await GetAsync(SearchUrl + "term=" + Uri.EscapeDataString(name) + "&entity=musicArtist&limit=5", cancellationToken).ConfigureAwait(false);
        if (result.Status != MetadataStatus.Ok)
        {
            return new MetadataResult<IReadOnlyList<MetadataArtist>>(result.Status);
        }

        var artists = result.Data!.Where(IsArtist).Select(ParseArtist).OfType<MetadataArtist>().ToList();
        return artists.Count == 0
            ? new MetadataResult<IReadOnlyList<MetadataArtist>>(MetadataStatus.NotFound)
            : new MetadataResult<IReadOnlyList<MetadataArtist>>(MetadataStatus.Ok, artists);
    }

    public Task<MetadataResult<IReadOnlyList<MetadataAlbum>>> GetArtistAlbumsAsync(string artistId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artistId);
        return AlbumsAsync(LookupUrl + "id=" + Uri.EscapeDataString(artistId) + "&entity=album&limit=200", cancellationToken);
    }

    private async Task<MetadataResult<IReadOnlyList<MetadataAlbum>>> AlbumsAsync(string url, CancellationToken cancellationToken)
    {
        var result = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (result.Status != MetadataStatus.Ok)
        {
            return new MetadataResult<IReadOnlyList<MetadataAlbum>>(result.Status);
        }

        var albums = result.Data!.Where(IsStudioAlbum).Select(ParseAlbum).OfType<MetadataAlbum>().ToList();
        return albums.Count == 0
            ? new MetadataResult<IReadOnlyList<MetadataAlbum>>(MetadataStatus.NotFound)
            : new MetadataResult<IReadOnlyList<MetadataAlbum>>(MetadataStatus.Ok, albums);
    }

    private async Task<MetadataResult<IReadOnlyList<JsonElement>>> GetAsync(string url, CancellationToken cancellationToken)
    {
        if (IsHeld())
        {
            return new MetadataResult<IReadOnlyList<JsonElement>>(MetadataStatus.Busy);
        }

        await PaceAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                Hold();
                _logger.LogWarning("iTunes answered {Status}; calls are held for {Seconds} s", (int)response.StatusCode, (int)BusyHold.TotalSeconds);
                return new MetadataResult<IReadOnlyList<JsonElement>>(MetadataStatus.Busy);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("iTunes answered {Status}", (int)response.StatusCode);
                return new MetadataResult<IReadOnlyList<JsonElement>>(MetadataStatus.Failed);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new MetadataResult<IReadOnlyList<JsonElement>>(MetadataStatus.Ok, ResultsOf(body));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "An iTunes call failed");
            return new MetadataResult<IReadOnlyList<JsonElement>>(MetadataStatus.Failed);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "An iTunes call timed out; it is tried again later");
            return new MetadataResult<IReadOnlyList<JsonElement>>(MetadataStatus.Busy);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "iTunes answered with a body that is not the JSON expected");
            return new MetadataResult<IReadOnlyList<JsonElement>>(MetadataStatus.Failed);
        }
    }

    // Each call takes the next free slot, so calls from every caller of this process stay Spacing apart.
    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        TimeSpan wait;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var slot = _nextCall > now ? _nextCall : now;
            _nextCall = slot + Spacing;
            wait = slot - now;
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsHeld()
    {
        lock (_gate)
        {
            return _time.GetUtcNow() < _heldUntil;
        }
    }

    private void Hold()
    {
        var until = _time.GetUtcNow() + BusyHold;
        lock (_gate)
        {
            _heldUntil = until;
        }
    }

    private static IReadOnlyList<JsonElement> ResultsOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var results = new List<JsonElement>();
        if (doc.RootElement.TryGetProperty("results", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            results.AddRange(array.EnumerateArray().Select(element => element.Clone()));
        }

        return results;
    }

    // The album rows of a lookup: the collection items that are albums (the artist row and the other kinds are skipped).
    private static bool IsStudioAlbum(JsonElement element) =>
        Text(element, "wrapperType") == "collection" && Text(element, "collectionType") == "Album";

    private static bool IsArtist(JsonElement element) => Number(element, "artistId") is not null && Text(element, "artistName") is not null;

    private static MetadataArtist? ParseArtist(JsonElement element) =>
        Number(element, "artistId") is { } id && Text(element, "artistName") is { } name ? new MetadataArtist(id, name) : null;

    private static MetadataAlbum? ParseAlbum(JsonElement element)
    {
        if (Number(element, "collectionId") is not { } id
            || Text(element, "collectionName") is not { } title
            || Text(element, "artistName") is not { } artist)
        {
            return null;
        }

        return new MetadataAlbum
        {
            Id = id,
            Title = title,
            ArtistName = artist,
            ArtistId = Number(element, "artistId"),
            ReleaseYear = ReleaseYear(Text(element, "releaseDate")),
            AlbumType = "album",
            CoverUrl = LargeCover(Text(element, "artworkUrl100")),
            Genre = Text(element, "primaryGenreName"),
            ReleaseDate = MetadataDates.FullDate(Text(element, "releaseDate")),
            TrackCount = Count(element, "trackCount"),
        };
    }

    private static int? Count(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) ? count : null;

    private static string? LargeCover(string? url) => url?.Replace(SmallArt, LargeArt, StringComparison.Ordinal);

    // Release dates are "1992-05-12T07:00:00Z": the year is the first four characters.
    private static int? ReleaseYear(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? (int?)year : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : null;
}
