using System.IO;
using Microsoft.Data.Sqlite;

namespace SRdeckPlugin.Wpf;

public sealed record GeoMapCachedTile(
    byte[] Content,
    string ContentType,
    string? ETag,
    DateTimeOffset? LastModified,
    DateTimeOffset ExpiresUtc)
{
    public bool IsFresh(DateTimeOffset now) => now < ExpiresUtc;
    public GeoMapTile ToTile() => new(Content, ContentType);
}

public sealed record GeoMapTileCacheStatistics(long TileCount, long ContentBytes);
public sealed record GeoMapTileCacheEntry(string Provider, int Zoom, int X, int Y,
    long ContentBytes, DateTimeOffset StoredUtc, DateTimeOffset ExpiresUtc);

public sealed class GeoMapTileCache : IDisposable
{
    public const long DefaultMaximumBytes = 512L * 1024 * 1024;

    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly long maximumBytes;
    private bool disposed;

    public GeoMapTileCache(string path, long maximumBytes = DefaultMaximumBytes)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A cache path is required.", nameof(path));
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        this.maximumBytes = maximumBytes;
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        try
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA busy_timeout = 5000;
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                CREATE TABLE IF NOT EXISTS tile_cache (
                    provider TEXT NOT NULL,
                    zoom_level INTEGER NOT NULL,
                    tile_column INTEGER NOT NULL,
                    tile_row INTEGER NOT NULL,
                    tile_data BLOB NOT NULL,
                    content_type TEXT NOT NULL,
                    etag TEXT NULL,
                    last_modified_utc INTEGER NULL,
                    expires_utc INTEGER NOT NULL,
                    stored_utc INTEGER NOT NULL,
                    last_access_utc INTEGER NOT NULL,
                    PRIMARY KEY (provider, zoom_level, tile_column, tile_row));
                CREATE INDEX IF NOT EXISTS tile_cache_access
                    ON tile_cache(last_access_utc);
                """;
            command.ExecuteNonQuery();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static string GetDefaultPath(string? localAppData = null)
    {
        string root = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "SRdeck", "maps", "tile-cache.sqlite");
    }

    public async ValueTask<GeoMapCachedTile?> GetTileAsync(
        string provider, int zoom, int x, int y, DateTimeOffset accessedUtc,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateProvider(provider);
        if (!GeoMapTileContent.IsValidCoordinate(zoom, x, y)) return null;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT tile_data, content_type, etag, last_modified_utc, expires_utc
                FROM tile_cache
                WHERE provider = $provider AND zoom_level = $zoom
                  AND tile_column = $x AND tile_row = $y
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$provider", provider);
            command.Parameters.AddWithValue("$zoom", zoom);
            command.Parameters.AddWithValue("$x", x);
            command.Parameters.AddWithValue("$y", y);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;

            byte[] content = (byte[])reader[0];
            string contentType = reader.GetString(1);
            string? etag = reader.IsDBNull(2) ? null : reader.GetString(2);
            DateTimeOffset? lastModified = reader.IsDBNull(3)
                ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3));
            DateTimeOffset expires = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4));
            await reader.DisposeAsync().ConfigureAwait(false);

            await using SqliteCommand touch = connection.CreateCommand();
            touch.CommandText = """
                UPDATE tile_cache SET last_access_utc = $accessed
                WHERE provider = $provider AND zoom_level = $zoom
                  AND tile_column = $x AND tile_row = $y
                """;
            touch.Parameters.AddWithValue("$accessed", accessedUtc.ToUnixTimeSeconds());
            touch.Parameters.AddWithValue("$provider", provider);
            touch.Parameters.AddWithValue("$zoom", zoom);
            touch.Parameters.AddWithValue("$x", x);
            touch.Parameters.AddWithValue("$y", y);
            await touch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (content.Length is 0 or > GeoMapTileContent.MaximumTileBytes ||
                !string.Equals(GeoMapTileContent.DetectContentType(content), contentType, StringComparison.OrdinalIgnoreCase))
                return null;
            return new(content, contentType, etag, lastModified, expires);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask StoreTileAsync(
        string provider, int zoom, int x, int y, GeoMapTile tile,
        string? etag, DateTimeOffset? lastModified, DateTimeOffset expiresUtc,
        DateTimeOffset storedUtc, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateProvider(provider);
        if (!GeoMapTileContent.IsValidCoordinate(zoom, x, y))
            throw new ArgumentOutOfRangeException(nameof(zoom), "The tile coordinate is outside the supported range.");
        ArgumentNullException.ThrowIfNull(tile);
        if (tile.Content.Length is 0 or > GeoMapTileContent.MaximumTileBytes ||
            !string.Equals(GeoMapTileContent.DetectContentType(tile.Content), tile.ContentType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The tile content is not a supported raster image.");

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO tile_cache (
                    provider, zoom_level, tile_column, tile_row, tile_data, content_type,
                    etag, last_modified_utc, expires_utc, stored_utc, last_access_utc)
                VALUES ($provider, $zoom, $x, $y, $data, $contentType,
                    $etag, $lastModified, $expires, $stored, $stored)
                ON CONFLICT(provider, zoom_level, tile_column, tile_row) DO UPDATE SET
                    tile_data = excluded.tile_data,
                    content_type = excluded.content_type,
                    etag = excluded.etag,
                    last_modified_utc = excluded.last_modified_utc,
                    expires_utc = excluded.expires_utc,
                    stored_utc = excluded.stored_utc,
                    last_access_utc = excluded.last_access_utc
                """;
            command.Parameters.AddWithValue("$provider", provider);
            command.Parameters.AddWithValue("$zoom", zoom);
            command.Parameters.AddWithValue("$x", x);
            command.Parameters.AddWithValue("$y", y);
            command.Parameters.AddWithValue("$data", tile.Content);
            command.Parameters.AddWithValue("$contentType", tile.ContentType);
            command.Parameters.AddWithValue("$etag", (object?)etag ?? DBNull.Value);
            command.Parameters.AddWithValue("$lastModified", lastModified is null
                ? DBNull.Value : lastModified.Value.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$expires", expiresUtc.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$stored", storedUtc.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await EvictToQuotaAsync(transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM tile_cache";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<GeoMapTileCacheStatistics> GetStatisticsAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*), COALESCE(SUM(length(tile_data)), 0)
                FROM tile_cache
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return new(0, 0);
            return new(reader.GetInt64(0), reader.GetInt64(1));
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<GeoMapTileCacheEntry>> ListTilesAsync(
        string provider, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateProvider(provider);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tiles = new List<GeoMapTileCacheEntry>();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT zoom_level, tile_column, tile_row, length(tile_data), stored_utc, expires_utc
                FROM tile_cache WHERE provider = $provider
                ORDER BY zoom_level, tile_column, tile_row
                """;
            command.Parameters.AddWithValue("$provider", provider);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                tiles.Add(new(provider, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                    reader.GetInt64(3), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5))));
            return tiles;
        }
        finally { gate.Release(); }
    }

    public async ValueTask<int> DeleteTilesAsync(
        IEnumerable<GeoMapTileCacheEntry> tiles, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(tiles);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM tile_cache WHERE provider = $provider AND zoom_level = $zoom
                    AND tile_column = $x AND tile_row = $y
                """;
            var provider = command.Parameters.Add("$provider", SqliteType.Text);
            var zoom = command.Parameters.Add("$zoom", SqliteType.Integer);
            var x = command.Parameters.Add("$x", SqliteType.Integer);
            var y = command.Parameters.Add("$y", SqliteType.Integer);
            int deleted = 0;
            foreach (GeoMapTileCacheEntry tile in tiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateProvider(tile.Provider);
                if (!GeoMapTileContent.IsValidCoordinate(tile.Zoom, tile.X, tile.Y))
                    throw new ArgumentOutOfRangeException(nameof(tiles));
                provider.Value = tile.Provider;
                zoom.Value = tile.Zoom;
                x.Value = tile.X;
                y.Value = tile.Y;
                deleted += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return deleted;
        }
        finally { gate.Release(); }
    }

    private async Task EvictToQuotaAsync(SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using SqliteCommand sizeCommand = connection.CreateCommand();
        sizeCommand.Transaction = transaction;
        sizeCommand.CommandText = "SELECT COALESCE(SUM(length(tile_data)), 0) FROM tile_cache";
        long currentBytes = Convert.ToInt64(await sizeCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        while (currentBytes > maximumBytes)
        {
            var oldest = new List<(long RowId, long Bytes)>();
            await using (SqliteCommand select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT rowid, length(tile_data) FROM tile_cache
                    ORDER BY last_access_utc ASC, stored_utc ASC
                    LIMIT 64
                    """;
                await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    oldest.Add((reader.GetInt64(0), reader.GetInt64(1)));
            }
            if (oldest.Count == 0) break;

            foreach ((long rowId, long bytes) in oldest)
            {
                await using SqliteCommand delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM tile_cache WHERE rowid = $rowid";
                delete.Parameters.AddWithValue("$rowid", rowId);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                currentBytes -= bytes;
                if (currentBytes <= maximumBytes) break;
            }
        }
    }

    private static void ValidateProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider) || provider.Length > 100)
            throw new ArgumentException("A bounded provider ID is required.", nameof(provider));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        connection.Dispose();
        gate.Dispose();
    }
}
