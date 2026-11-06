using System.Collections.Concurrent;

namespace octo_fiesta.Services.Common;

/// <summary>
/// Cover art of provider artists, albums, songs and playlists, kept in memory so the
/// now-playing screen gets its image at once, even while the track itself is still downloading.
/// </summary>
public sealed class ExternalCoverArtService
{
    public sealed record CoverImage(byte[] Data, string ContentType);

    // What players show on a now-playing screen. Bigger is only sent when asked for.
    private const int DisplaySize = 600;
    private const int ThumbnailSize = 300;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private const int MaxCachedCovers = 256;

    // Used when no IHttpClientFactory is given (tests, manual construction)
    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

    private readonly IMusicMetadataService _metadataService;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _covers = new();

    private sealed record CacheEntry(Task<CoverImage?> Image, DateTime Expiry);

    public ExternalCoverArtService(IMusicMetadataService metadataService, ILogger<ExternalCoverArtService> logger,
        IHttpClientFactory? httpClientFactory = null)
    {
        _metadataService = metadataService;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// The cover for an external id of the given type ("artist", "album" or "song"), or null.
    /// <paramref name="size"/> is the Subsonic size parameter, the largest side the client wants.
    /// </summary>
    public Task<CoverImage?> GetAsync(string provider, string type, string externalId, int? size) =>
        GetCachedAsync($"{provider}|{type}|{externalId}|{SizeBucket(size)}",
            () => ResolveUrlAsync(provider, type, externalId, size));

    public Task<CoverImage?> GetPlaylistCoverAsync(string provider, string externalId) =>
        GetCachedAsync($"{provider}|playlist|{externalId}",
            async () => (await _metadataService.GetPlaylistAsync(provider, externalId))?.CoverUrl);

    /// <summary>
    /// Starts fetching a song's cover in the background, so it is ready by the time the
    /// player asks for it, which it usually does right as playback starts.
    /// </summary>
    public void Prefetch(string provider, string songExternalId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await GetAsync(provider, "song", songExternalId, size: null);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Cover prefetch failed for {Provider}:{Id}", provider, songExternalId);
            }
        });
    }

    private async Task<CoverImage?> GetCachedAsync(string key, Func<Task<string?>> resolveUrl)
    {
        var now = DateTime.UtcNow;
        TrimCache(now);

        var entry = _covers.AddOrUpdate(key,
            _ => new CacheEntry(FetchAsync(resolveUrl), now + CacheTtl),
            (_, existing) => existing.Expiry > now && !existing.Image.IsFaulted
                ? existing
                : new CacheEntry(FetchAsync(resolveUrl), now + CacheTtl));

        CoverImage? image;
        try
        {
            image = await entry.Image;
        }
        catch
        {
            _covers.TryRemove(new KeyValuePair<string, CacheEntry>(key, entry));
            throw;
        }

        if (image == null)
        {
            // Not cached: a cover that failed is asked again next time
            _covers.TryRemove(new KeyValuePair<string, CacheEntry>(key, entry));
        }
        return image;
    }

    private async Task<CoverImage?> FetchAsync(Func<Task<string?>> resolveUrl)
    {
        // Off the caller's stack, so the shared task never carries the first caller's state
        await Task.Yield();

        var url = await resolveUrl();
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var client = _httpClientFactory?.CreateClient("CoverArtClient") ?? SharedClient.Value;
        using var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Cover art fetch failed for {Url}: HTTP {Status}", url, (int)response.StatusCode);
            return null;
        }

        var data = await response.Content.ReadAsByteArrayAsync();
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
        return new CoverImage(data, contentType);
    }

    private async Task<string?> ResolveUrlAsync(string provider, string type, string externalId, int? size)
    {
        switch (type)
        {
            case "artist":
                return (await _metadataService.GetArtistAsync(provider, externalId))?.ImageUrl;

            case "album":
                var album = await _metadataService.GetAlbumAsync(provider, externalId);
                return album?.CoverArtUrl == null ? null : PickUrl(album.CoverArtUrl, album.CoverArtUrlLarge, size);

            default:
                var song = await _metadataService.GetSongAsync(provider, externalId);
                if (song?.CoverArtUrl != null)
                {
                    return PickUrl(song.CoverArtUrl, song.CoverArtUrlLarge, size);
                }

                var albumFallback = await _metadataService.GetAlbumAsync(provider, externalId);
                return albumFallback?.CoverArtUrl == null ? null : PickUrl(albumFallback.CoverArtUrl, albumFallback.CoverArtUrlLarge, size);
        }
    }

    /// <summary>
    /// The image for the size the client asked for. The large image can be the provider's
    /// untouched original (Qobuz "_org", often several MB), which takes long to arrive and
    /// competes with the track download for bandwidth, so it is only sent when asked for.
    /// </summary>
    internal static string PickUrl(string small, string? large, int? size)
    {
        if (size is > DisplaySize)
        {
            return large ?? small;
        }
        if (size is > 0 and <= ThumbnailSize)
        {
            return small;
        }
        return DisplayVersion(large) ?? small;
    }

    // Qobuz serves every cover at 600 px next to its original
    private static string? DisplayVersion(string? url) =>
        url != null && url.EndsWith("_org.jpg", StringComparison.OrdinalIgnoreCase)
            ? url[..^"_org.jpg".Length] + "_600.jpg"
            : url;

    private static string SizeBucket(int? size) => size switch
    {
        > DisplaySize => "large",
        > 0 and <= ThumbnailSize => "thumbnail",
        _ => "display"
    };

    private void TrimCache(DateTime now)
    {
        if (_covers.Count < MaxCachedCovers)
        {
            return;
        }

        foreach (var (key, value) in _covers)
        {
            if (value.Expiry <= now)
            {
                _covers.TryRemove(new KeyValuePair<string, CacheEntry>(key, value));
            }
        }

        // Still full: drop the oldest half
        if (_covers.Count >= MaxCachedCovers)
        {
            foreach (var (key, value) in _covers.OrderBy(e => e.Value.Expiry).Take(MaxCachedCovers / 2).ToList())
            {
                _covers.TryRemove(new KeyValuePair<string, CacheEntry>(key, value));
            }
        }
    }
}
