namespace SRdeckPlugin.Wpf;

internal static class GeoMapTileContent
{
    public const int MaximumTileBytes = 5 * 1024 * 1024;

    public static bool IsValidCoordinate(int zoom, int x, int y)
    {
        if (zoom is < 0 or > 22 || x < 0 || y < 0) return false;
        long dimension = 1L << zoom;
        return x < dimension && y < dimension;
    }

    public static string DetectContentType(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith<byte>([0x89, 0x50, 0x4e, 0x47])) return "image/png";
        if (data.StartsWith<byte>([0xff, 0xd8, 0xff])) return "image/jpeg";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        return string.Empty;
    }
}
