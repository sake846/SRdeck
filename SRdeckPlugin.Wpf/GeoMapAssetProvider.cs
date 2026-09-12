using System.IO;

namespace SRdeckPlugin.Wpf;

public static class GeoMapAssetProvider
{
    public const string HostName = "srdeck-map-assets.local";
    private static readonly IReadOnlyDictionary<string, Lazy<GeoMapTile?>> Assets =
        new Dictionary<string, Lazy<GeoMapTile?>>(StringComparer.Ordinal)
        {
            ["/leaflet.css"] = new(() => Load("SRdeckPlugin.Wpf.MapAssets.leaflet.leaflet.css", "text/css; charset=utf-8")),
            ["/leaflet.js"] = new(() => Load("SRdeckPlugin.Wpf.MapAssets.leaflet.leaflet.js", "text/javascript; charset=utf-8"))
        };

    public static bool IsAssetUri(string value) => TryGetAssetPath(value, out _);

    public static GeoMapTile? GetAsset(string value)
    {
        return TryGetAssetPath(value, out string path) && Assets.TryGetValue(path, out Lazy<GeoMapTile?>? asset)
            ? asset.Value : null;
    }

    private static bool TryGetAssetPath(string value, out string path)
    {
        path = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !Assets.ContainsKey(uri.AbsolutePath)) return false;
        path = uri.AbsolutePath;
        return true;
    }

    private static GeoMapTile? Load(string resourceName, string contentType)
    {
        using Stream? stream = typeof(GeoMapAssetProvider).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return new(output.ToArray(), contentType);
    }
}
