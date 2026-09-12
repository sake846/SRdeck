using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace SRdeckPlugin.Wpf;

public sealed record OnlineGeoMapTileResult(GeoMapTile? Tile, bool FromCache, bool NetworkFailed);

public sealed class OnlineGeoMapTileProvider
{
    public const string ProviderId = "openstreetmap-standard-v1";
    private const string ProductToken = "SRdeck/1.0 (+https://github.com/sake846/SRdeck)";
    private static readonly TimeSpan DefaultCacheLifetime = TimeSpan.FromDays(7);
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly GeoMapTileCache? cache;
    private readonly HttpClient httpClient;
    private readonly Func<DateTimeOffset> utcNow;

    public OnlineGeoMapTileProvider(
        GeoMapTileCache? cache,
        HttpClient? httpClient = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.cache = cache;
        this.httpClient = httpClient ?? SharedHttpClient;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async ValueTask<OnlineGeoMapTileResult> GetTileAsync(
        int zoom, int x, int y, bool allowNetwork, CancellationToken cancellationToken)
    {
        if (!GeoMapTileContent.IsValidCoordinate(zoom, x, y)) return new(null, false, false);
        DateTimeOffset now = utcNow();
        GeoMapCachedTile? cached = await TryGetCachedTileAsync(zoom, x, y, now, cancellationToken)
            .ConfigureAwait(false);
        if (cached?.IsFresh(now) == true) return new(cached.ToTile(), true, false);
        if (!allowNetwork) return new(null, false, false);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"https://tile.openstreetmap.org/{zoom}/{x}/{y}.png");
            request.Headers.UserAgent.ParseAdd(ProductToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
            if (!string.IsNullOrWhiteSpace(cached?.ETag) &&
                EntityTagHeaderValue.TryParse(cached.ETag, out EntityTagHeaderValue? entityTag))
                request.Headers.IfNoneMatch.Add(entityTag);
            if (cached?.LastModified is not null) request.Headers.IfModifiedSince = cached.LastModified;

            using HttpResponseMessage response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                DateTimeOffset expires = GetExpiration(response, now);
                await TryStoreTileAsync(zoom, x, y, cached.ToTile(),
                    response.Headers.ETag?.ToString() ?? cached.ETag,
                    response.Content.Headers.LastModified ?? cached.LastModified,
                    expires, now, response.Headers.CacheControl?.NoStore == true, cancellationToken)
                    .ConfigureAwait(false);
                return new(cached.ToTile(), true, false);
            }
            if (response.StatusCode != HttpStatusCode.OK) return new(null, false, true);
            if (response.Content.Headers.ContentLength is long length &&
                length is <= 0 or > GeoMapTileContent.MaximumTileBytes)
                return new(null, false, false);

            byte[] content = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            string contentType = GeoMapTileContent.DetectContentType(content);
            if (contentType.Length == 0) return new(null, false, false);
            var tile = new GeoMapTile(content, contentType);
            await TryStoreTileAsync(zoom, x, y, tile,
                response.Headers.ETag?.ToString(), response.Content.Headers.LastModified,
                GetExpiration(response, now), now,
                response.Headers.CacheControl?.NoStore == true, cancellationToken).ConfigureAwait(false);
            return new(tile, false, false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            return new(null, false, true);
        }
    }

    private async ValueTask<GeoMapCachedTile?> TryGetCachedTileAsync(
        int zoom, int x, int y, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (cache is null) return null;
        try
        {
            return await cache.GetTileAsync(ProviderId, zoom, x, y, now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
        {
            return null;
        }
    }

    private async ValueTask TryStoreTileAsync(
        int zoom, int x, int y, GeoMapTile tile, string? etag,
        DateTimeOffset? lastModified, DateTimeOffset expires, DateTimeOffset stored,
        bool noStore, CancellationToken cancellationToken)
    {
        if (cache is null || noStore) return;
        try
        {
            await cache.StoreTileAsync(ProviderId, zoom, x, y, tile, etag,
                lastModified, expires, stored, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
        {
        }
    }

    private static DateTimeOffset GetExpiration(HttpResponseMessage response, DateTimeOffset now)
    {
        CacheControlHeaderValue? cacheControl = response.Headers.CacheControl;
        if (cacheControl?.NoCache == true) return now;
        if (cacheControl?.MaxAge is TimeSpan maxAge)
        {
            DateTimeOffset originDate = response.Headers.Date ?? now - (response.Headers.Age ?? TimeSpan.Zero);
            DateTimeOffset expires = originDate + maxAge;
            return expires > now ? expires : now;
        }
        if (response.Content.Headers.Expires is DateTimeOffset expiresHeader)
            return expiresHeader > now ? expiresHeader : now;
        return now + DefaultCacheLifetime;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > GeoMapTileContent.MaximumTileBytes)
                throw new InvalidDataException("The map tile exceeded the size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        MaxConnectionsPerServer = 8,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
}
