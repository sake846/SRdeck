using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace SRdeckPlugin.Wpf;

public sealed record OfflineGeoMapDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Percentage => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0d, 100d)
        : null;
}

/// <summary>Downloads a raster MBTiles archive without loading it into memory.</summary>
public sealed class OfflineGeoMapDownloader
{
    private const string ProductToken = "SRdeck/1.0 (+https://github.com/sake846/SRdeck)";
    private const int BufferSize = 128 * 1024;
    private const long ProgressIntervalBytes = 1024 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly HttpClient httpClient;

    public OfflineGeoMapDownloader(HttpClient? httpClient = null) =>
        this.httpClient = httpClient ?? SharedHttpClient;

    public static string GetDefaultDirectory(string? localAppData = null)
    {
        string root = localAppData ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "SRdeck", "maps", "offline");
    }

    public static bool IsSupportedSourceUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo);

    public async Task DownloadAsync(
        Uri sourceUri,
        string destinationPath,
        IProgress<OfflineGeoMapDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupportedSourceUri(sourceUri))
            throw new ArgumentException("The offline map URL must use HTTPS and must not contain credentials.",
                nameof(sourceUri));
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("A destination path is required.", nameof(destinationPath));

        string fullDestinationPath = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(fullDestinationPath);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("The destination path must include a directory.", nameof(destinationPath));

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory,
            $".{Path.GetFileName(fullDestinationPath)}.{Guid.NewGuid():N}.download");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
            request.Headers.UserAgent.ParseAdd(ProductToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            using HttpResponseMessage response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            Uri? finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is not null && !IsSupportedSourceUri(finalUri))
                throw new HttpRequestException("The offline map download was redirected to an unsafe URL.");
            if (response.Content.Headers.ContentLength is <= 0)
                throw new InvalidDataException("The offline map download was empty.");

            long? totalBytes = response.Content.Headers.ContentLength;
            long bytesReceived = 0;
            long lastReportedBytes = 0;
            var reportTimer = Stopwatch.StartNew();
            progress?.Report(new(0, totalBytes));

            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[BufferSize];
                while (true)
                {
                    int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    bytesReceived += read;

                    if (bytesReceived - lastReportedBytes >= ProgressIntervalBytes ||
                        reportTimer.Elapsed >= ProgressInterval)
                    {
                        progress?.Report(new(bytesReceived, totalBytes));
                        lastReportedBytes = bytesReceived;
                        reportTimer.Restart();
                    }
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (bytesReceived == 0 || totalBytes is long expectedBytes && bytesReceived != expectedBytes)
                throw new InvalidDataException("The offline map download did not complete.");

            cancellationToken.ThrowIfCancellationRequested();
            using (var provider = new MbTilesTileProvider(temporaryPath))
            {
                _ = provider.Metadata;
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullDestinationPath, true);
            progress?.Report(new(bytesReceived, totalBytes ?? bytesReceived));
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        MaxConnectionsPerServer = 2,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
}
