/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WebCore.Metadata;

namespace WebCore.Spotify;

/// <summary>
/// Spotify's catalog reads over one plain HttpClient, with a client-credentials token cached until 60 s before it expires (refreshed under a lock). An HTTP 429
/// holds every call for its Retry-After (30 s when none is given) and answers Busy meanwhile. This app's limit is 10 items a page, so pages are read with
/// limit 10 until the answer has no next page, up to ten pages. Never throws for a network or status failure, and never logs the secret or the token.
/// </summary>
public sealed class SpotifyCatalogClient : IAlbumMetadataSource, IDisposable
{
    public const int PageSize = 10;

    public const int MaxPages = 10;

    private const string TokenUrl = "https://accounts.spotify.com/api/token";

    private const string ApiUrl = "https://api.spotify.com/v1/";

    private const string Market = "US";

    private static readonly TimeSpan TokenMargin = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan DefaultHold = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly SpotifySettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<SpotifyCatalogClient> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly object _gate = new();
    private string? _token;
    private DateTimeOffset _tokenUntil;
    private DateTimeOffset _heldUntil;

    public SpotifyCatalogClient(HttpClient http, SpotifySettings settings, TimeProvider time, ILogger<SpotifyCatalogClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (!settings.IsConfigured)
        {
            logger.LogInformation("Spotify is not configured (Spotify:ClientId and Spotify:ClientSecret); the Spotify fallback is off");
        }
    }

    public string Name => "spotify";

    /// <summary>Releases the token lock; the client is a process-wide singleton, so the container calls this at shutdown.</summary>
    public void Dispose() => _tokenLock.Dispose();

    public Task<MetadataResult<IReadOnlyList<MetadataAlbum>>> SearchAlbumsByUpcAsync(string upc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upc);
        return upc.Length > 0 && upc.All(char.IsAsciiDigit)
            ? SearchAlbumsAsync("upc:" + upc, cancellationToken)
            : Task.FromResult(new MetadataResult<IReadOnlyList<MetadataAlbum>>(MetadataStatus.NotFound));
    }

    public async Task<MetadataResult<IReadOnlyList<MetadataArtist>>> SearchArtistAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var url = ApiUrl + "search?type=artist&limit=" + PageSize + "&offset=0&q=" + Uri.EscapeDataString(name);
        var result = await GetAsync(url, ArtistPageIn, cancellationToken).ConfigureAwait(false);
        return AsList(result);
    }

    public async Task<MetadataResult<IReadOnlyList<MetadataAlbum>>> GetArtistAlbumsAsync(string artistId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artistId);
        var albums = new List<MetadataAlbum>();
        for (var page = 0; page < MaxPages; page++)
        {
            var url = ApiUrl + "artists/" + Uri.EscapeDataString(artistId) + "/albums?include_groups=album&market=" + Market
                + "&limit=" + PageSize + "&offset=" + page * PageSize;
            var result = await GetAsync(url, AlbumPageIn, cancellationToken).ConfigureAwait(false);
            if (result.Status != MetadataStatus.Ok)
            {
                return new MetadataResult<IReadOnlyList<MetadataAlbum>>(result.Status);
            }

            albums.AddRange(result.Data!.Items);
            if (!result.Data.HasNext)
            {
                break;
            }
        }

        return albums.Count == 0
            ? new MetadataResult<IReadOnlyList<MetadataAlbum>>(MetadataStatus.NotFound)
            : new MetadataResult<IReadOnlyList<MetadataAlbum>>(MetadataStatus.Ok, albums);
    }

    /// <summary>The first page of an album search for the text (Spotify's own search; not part of the shared metadata interface).</summary>
    public async Task<MetadataResult<IReadOnlyList<MetadataAlbum>>> SearchAlbumsAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var url = ApiUrl + "search?type=album&limit=" + PageSize + "&offset=0&q=" + Uri.EscapeDataString(query);
        var result = await GetAsync(url, AlbumPageIn, cancellationToken).ConfigureAwait(false);
        return AsList(result);
    }

    // One page of a list: its items, and whether Spotify names a next page.
    private sealed record Page<T>(IReadOnlyList<T> Items, bool HasNext) where T : class;

    private static MetadataResult<IReadOnlyList<T>> AsList<T>(MetadataResult<Page<T>> result) where T : class
    {
        if (result.Status != MetadataStatus.Ok)
        {
            return new MetadataResult<IReadOnlyList<T>>(result.Status);
        }

        return result.Data!.Items.Count == 0
            ? new MetadataResult<IReadOnlyList<T>>(MetadataStatus.NotFound)
            : new MetadataResult<IReadOnlyList<T>>(MetadataStatus.Ok, result.Data.Items);
    }

    private async Task<MetadataResult<Page<T>>> GetAsync<T>(string url, Func<JsonElement, Page<T>> parse, CancellationToken cancellationToken) where T : class
    {
        if (!_settings.IsConfigured)
        {
            return new MetadataResult<Page<T>>(MetadataStatus.Failed);
        }

        if (IsHeld())
        {
            return new MetadataResult<Page<T>>(MetadataStatus.Busy);
        }

        var token = await TokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return new MetadataResult<Page<T>>(MetadataStatus.Failed);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await SendAsync(request, parse, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MetadataResult<Page<T>>> SendAsync<T>(HttpRequestMessage request, Func<JsonElement, Page<T>> parse, CancellationToken cancellationToken) where T : class
    {
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return await ReadAsync(response, parse, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "A Spotify call failed");
            return new MetadataResult<Page<T>>(MetadataStatus.Failed);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A timeout arrives as a cancellation while the caller is still live: Spotify is slow, so the caller is told to try later.
            _logger.LogWarning(ex, "A Spotify call timed out; it is tried again later");
            return new MetadataResult<Page<T>>(MetadataStatus.Busy);
        }
    }

    private async Task<MetadataResult<Page<T>>> ReadAsync<T>(HttpResponseMessage response, Func<JsonElement, Page<T>> parse, CancellationToken cancellationToken) where T : class
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            Hold(response.Headers.RetryAfter);
            return new MetadataResult<Page<T>>(MetadataStatus.Busy);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new MetadataResult<Page<T>>(MetadataStatus.NotFound);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Spotify answered {Status}", (int)response.StatusCode);
            return new MetadataResult<Page<T>>(MetadataStatus.Failed);
        }

        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return new MetadataResult<Page<T>>(MetadataStatus.Ok, parse(doc.RootElement));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Spotify answered with a body that is not the JSON expected");
            return new MetadataResult<Page<T>>(MetadataStatus.Failed);
        }
    }

    private bool IsHeld()
    {
        lock (_gate)
        {
            return _time.GetUtcNow() < _heldUntil;
        }
    }

    private void Hold(RetryConditionHeaderValue? retryAfter)
    {
        var now = _time.GetUtcNow();
        var wait = RetryWait(retryAfter, now);
        lock (_gate)
        {
            _heldUntil = now + wait;
        }

        _logger.LogWarning("Spotify answered 429; calls are held for {Seconds} s", (int)wait.TotalSeconds);
    }

    // Retry-After is seconds or a date; a missing or past one takes the default hold.
    private static TimeSpan RetryWait(RetryConditionHeaderValue? retryAfter, DateTimeOffset now)
    {
        TimeSpan? wait = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - now : (TimeSpan?)null);
        return wait is { } span && span > TimeSpan.Zero ? span : DefaultHold;
    }

    private async Task<string?> TokenAsync(CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is not null && _time.GetUtcNow() < _tokenUntil)
            {
                return _token;
            }

            return await RequestTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<string?> RequestTokenAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("grant_type", "client_credentials")]),
        };
        var pair = Encoding.UTF8.GetBytes(_settings.ClientId + ":" + _settings.ClientSecret);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(pair));
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Spotify refused the token request with {Status}", (int)response.StatusCode);
                return null;
            }

            return await KeepTokenAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogWarning(ex, "The Spotify token request failed");
            return null;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "The Spotify token request timed out");
            return null;
        }
    }

    private async Task<string?> KeepTokenAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("access_token", out var tokenElement) || tokenElement.ValueKind != JsonValueKind.String || tokenElement.GetString() is not { } token)
        {
            _logger.LogWarning("Spotify's token answer had no access token");
            return null;
        }

        var seconds = root.TryGetProperty("expires_in", out var expires) && expires.ValueKind == JsonValueKind.Number ? expires.GetInt32() : 3600;
        _token = token;
        _tokenUntil = _time.GetUtcNow() + TimeSpan.FromSeconds(seconds) - TokenMargin;
        return token;
    }

    private static Page<MetadataAlbum> AlbumPageIn(JsonElement root) => AlbumPage(Section(root, "albums"));

    private static Page<MetadataArtist> ArtistPageIn(JsonElement root) => ArtistPage(Section(root, "artists"));

    // A search answer keeps its list under "albums" or "artists"; an artist's albums answer is the list itself.
    private static JsonElement Section(JsonElement root, string name) => root.TryGetProperty(name, out var section) ? section : root;

    private static Page<MetadataAlbum> AlbumPage(JsonElement list) => new(Items(list, ParseAlbum), HasNext(list));

    private static Page<MetadataArtist> ArtistPage(JsonElement list) => new(Items(list, ParseArtist), HasNext(list));

    private static List<T> Items<T>(JsonElement list, Func<JsonElement, T?> parse) where T : class
    {
        var items = new List<T>();
        if (!list.TryGetProperty("items", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var element in array.EnumerateArray())
        {
            // Spotify returns null for an item it cannot show; those are skipped.
            if (element.ValueKind == JsonValueKind.Object && parse(element) is { } parsed)
            {
                items.Add(parsed);
            }
        }

        return items;
    }

    private static bool HasNext(JsonElement list) => list.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String;

    private static MetadataArtist? ParseArtist(JsonElement element) =>
        Text(element, "id") is { } id && Text(element, "name") is { } name ? new MetadataArtist(id, name) : null;

    private static MetadataAlbum? ParseAlbum(JsonElement element)
    {
        var id = Text(element, "id");
        var title = Text(element, "name");
        var type = Text(element, "album_type");
        var artist = FirstArtist(element);
        if (id is null || title is null || type is null || artist is null)
        {
            return null;
        }

        return new MetadataAlbum
        {
            Id = id,
            Title = title,
            ArtistName = artist.Name,
            ArtistId = artist.Id,
            ReleaseYear = ReleaseYear(Text(element, "release_date")),
            AlbumType = type,
            CoverUrl = LargestImage(element),
            Upc = element.TryGetProperty("external_ids", out var ids) ? Text(ids, "upc") : null,
        };
    }

    private static MetadataArtist? FirstArtist(JsonElement album) =>
        album.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array && artists.GetArrayLength() > 0
            ? ParseArtist(artists[0])
            : null;

    // Release dates are "1973", "1973-03" or "1973-03-01": the year is the first four characters.
    private static int? ReleaseYear(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? (int?)year : null;

    private static string? LargestImage(JsonElement album)
    {
        if (!album.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? best = null;
        var bestWidth = -1;
        foreach (var image in images.EnumerateArray())
        {
            var url = Text(image, "url");
            var width = image.TryGetProperty("width", out var widthElement) && widthElement.ValueKind == JsonValueKind.Number ? widthElement.GetInt32() : 0;
            if (url is not null && width > bestWidth)
            {
                best = url;
                bestWidth = width;
            }
        }

        return best;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
