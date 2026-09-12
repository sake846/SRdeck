using System.IO;
using Microsoft.Data.Sqlite;

namespace SRdeckPlugin.Wpf;

public sealed record MbTilesMetadata(
    string Name, string Format, int MinimumZoom, int MaximumZoom, string Attribution);

public sealed record GeoMapTile(byte[] Content, string ContentType);

public sealed class MbTilesTileProvider : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public MbTilesMetadata Metadata { get; }

    public MbTilesTileProvider(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("MBTiles path is required.", nameof(path));
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("MBTiles file was not found.", fullPath);

        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            // Downloaded archives are validated under a temporary name and then
            // atomically moved. A pooled read-only handle would keep that file locked.
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            ValidateSchema(connection);
            Metadata = ReadMetadata(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public async ValueTask<GeoMapTile?> GetTileAsync(int zoom, int x, int y, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!GeoMapTileContent.IsValidCoordinate(zoom, x, y)) return null;
        long dimension = 1L << zoom;
        long tmsY = dimension - 1 - y;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT tile_data FROM tiles
                WHERE zoom_level = $zoom AND tile_column = $x AND tile_row = $y
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$zoom", zoom);
            command.Parameters.AddWithValue("$x", x);
            command.Parameters.AddWithValue("$y", tmsY);
            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is not byte[] bytes || bytes.Length == 0 || bytes.Length > GeoMapTileContent.MaximumTileBytes) return null;
            string contentType = GeoMapTileContent.DetectContentType(bytes);
            return contentType.Length == 0 ? null : new(bytes, contentType);
        }
        finally
        {
            gate.Release();
        }
    }

    public static bool TryParseTileUri(string value, out int zoom, out int x, out int y)
    {
        zoom = x = y = 0;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("srdeck-map-tiles.local", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo)) return false;
        string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 && int.TryParse(parts[0], out zoom) &&
            int.TryParse(parts[1], out x) && int.TryParse(parts[2], out y) &&
            GeoMapTileContent.IsValidCoordinate(zoom, x, y);
    }

    private static void ValidateSchema(SqliteConnection database)
    {
        using SqliteCommand command = database.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('tiles') WHERE name IN ('zoom_level','tile_column','tile_row','tile_data')";
        if (Convert.ToInt32(command.ExecuteScalar()) != 4)
            throw new InvalidDataException("The file does not contain a compatible MBTiles tiles table.");
    }

    private static MbTilesMetadata ReadMetadata(SqliteConnection database)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using SqliteCommand command = database.CreateCommand();
        command.CommandText = "SELECT name, value FROM metadata";
        try
        {
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) values[reader.GetString(0)] = reader.GetString(1);
        }
        catch (SqliteException) { }

        string format = values.GetValueOrDefault("format", "png").ToLowerInvariant();
        if (format is not ("png" or "jpg" or "jpeg" or "webp"))
            throw new InvalidDataException($"Unsupported MBTiles format: {format}.");
        return new MbTilesMetadata(
            values.GetValueOrDefault("name", Path.GetFileNameWithoutExtension(database.DataSource)),
            format,
            ParseZoom(values.GetValueOrDefault("minzoom"), 0),
            ParseZoom(values.GetValueOrDefault("maxzoom"), 19),
            values.GetValueOrDefault("attribution", "Offline map"));
    }

    private static int ParseZoom(string? value, int fallback) =>
        int.TryParse(value, out int parsed) ? Math.Clamp(parsed, 0, 22) : fallback;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        connection.Dispose();
        gate.Dispose();
    }
}
