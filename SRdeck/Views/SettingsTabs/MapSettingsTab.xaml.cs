using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using SRdeckPlugin.Wpf;

namespace SRdeck.Views.SettingsTabs;

public partial class MapSettingsTab : UserControl
{
    private bool initializing;
    private GeoMapSourceOptions options = new();
    private CancellationTokenSource? mapDownloadCancellation;

    public MapSettingsTab()
    {
        InitializeComponent();
        LoadOptions();
        _ = UpdateCacheStatusAsync();
        Unloaded += (_, _) => mapDownloadCancellation?.Cancel();
    }

    private void LoadOptions()
    {
        initializing = true;
        try
        {
            options = GeoMapSourceStore.GetOptions();
            MapModeComboBox.SelectedValue = options.Mode.ToString();
            MbTilesPathTextBox.Text = options.MbTilesPath ?? string.Empty;
            UpdateValidationStatus();
        }
        finally
        {
            initializing = false;
        }
    }

    private void MapModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing || MapModeComboBox.SelectedValue is not string value ||
            !Enum.TryParse(value, out GeoMapSourceMode mode)) return;
        _ = Save(options with { Mode = mode });
    }

    private void SelectMbTilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = TryFindResource("Title_SelectMbTiles") as string ?? "Select offline map",
            Filter = "MBTiles (*.mbtiles)|*.mbtiles|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            using var provider = new MbTilesTileProvider(dialog.FileName);
            GeoMapSourceMode mode = options.Mode == GeoMapSourceMode.Online
                ? GeoMapSourceMode.Auto : options.Mode;
            if (Save(options with { MbTilesPath = Path.GetFullPath(dialog.FileName), Mode = mode }))
                LoadOptions();
        }
        catch (Exception exception) when (exception is IOException or SqliteException)
        {
            MessageBox.Show(exception.Message,
                TryFindResource("Title_MapError") as string ?? "Map settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool Save(GeoMapSourceOptions updated)
    {
        if (!GeoMapSourceStore.TrySaveOptions(updated))
        {
            MessageBox.Show(
                TryFindResource("Error_MapSettingsSave") as string ?? "The map settings could not be saved.",
                TryFindResource("Title_MapError") as string ?? "Map settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            LoadOptions();
            return false;
        }
        options = updated;
        MbTilesPathTextBox.Text = options.MbTilesPath ?? string.Empty;
        UpdateValidationStatus();
        return true;
    }

    private async void DownloadMapButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(MapDownloadUrlTextBox.Text.Trim(), UriKind.Absolute, out Uri? sourceUri) ||
            !OfflineGeoMapDownloader.IsSupportedSourceUri(sourceUri))
        {
            MessageBox.Show(
                TryFindResource("Error_MapDownloadUrl") as string ??
                "Enter an HTTPS URL for a raster MBTiles file.",
                TryFindResource("Title_MapDownload") as string ?? "Download offline map",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string defaultDirectory = OfflineGeoMapDownloader.GetDefaultDirectory();
        var dialog = new SaveFileDialog
        {
            Title = TryFindResource("Title_SaveDownloadedMbTiles") as string ?? "Save offline map",
            Filter = "MBTiles (*.mbtiles)|*.mbtiles|All files (*.*)|*.*",
            DefaultExt = ".mbtiles",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Directory.Exists(defaultDirectory) ? defaultDirectory : null,
            FileName = GetSuggestedFileName(sourceUri)
        };
        if (dialog.ShowDialog() != true) return;

        mapDownloadCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = mapDownloadCancellation.Token;
        SetDownloadState(true);
        MapDownloadProgressBar.Value = 0;
        MapDownloadStatusText.Text = TryFindResource("Status_MapDownloadStarting") as string ??
            "Starting download...";

        var progress = new Progress<OfflineGeoMapDownloadProgress>(value =>
        {
            double megabytes = value.BytesReceived / 1024d / 1024d;
            if (value.Percentage is double percentage && value.TotalBytes is long totalBytes)
            {
                MapDownloadProgressBar.IsIndeterminate = false;
                MapDownloadProgressBar.Value = percentage;
                MapDownloadStatusText.Text = string.Format(
                    TryFindResource("Status_MapDownloadingKnown") as string ??
                    "Downloading... {0:N1} / {1:N1} MB ({2:N0}%)",
                    megabytes, totalBytes / 1024d / 1024d, percentage);
            }
            else
            {
                MapDownloadProgressBar.IsIndeterminate = true;
                MapDownloadStatusText.Text = string.Format(
                    TryFindResource("Status_MapDownloadingUnknown") as string ??
                    "Downloading... {0:N1} MB", megabytes);
            }
        });

        try
        {
            var downloader = new OfflineGeoMapDownloader();
            await downloader.DownloadAsync(sourceUri, dialog.FileName, progress, cancellationToken);

            string downloadedPath = Path.GetFullPath(dialog.FileName);
            GeoMapSourceMode mode = options.Mode == GeoMapSourceMode.Online
                ? GeoMapSourceMode.Auto : options.Mode;
            if (Save(options with { MbTilesPath = downloadedPath, Mode = mode }))
            {
                LoadOptions();
                MapDownloadProgressBar.IsIndeterminate = false;
                MapDownloadProgressBar.Value = 100;
                MapDownloadStatusText.Text = string.Format(
                    TryFindResource("Status_MapDownloadComplete") as string ??
                    "Download complete: {0}", downloadedPath);
            }
        }
        catch (OperationCanceledException)
        {
            MapDownloadStatusText.Text = TryFindResource("Status_MapDownloadCanceled") as string ??
                "Download canceled.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            HttpRequestException or SqliteException or ArgumentException)
        {
            MapDownloadStatusText.Text = string.Format(
                TryFindResource("Status_MapDownloadFailed") as string ?? "Download failed: {0}",
                exception.Message);
            MessageBox.Show(exception.Message,
                TryFindResource("Title_MapDownload") as string ?? "Download offline map",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            mapDownloadCancellation.Dispose();
            mapDownloadCancellation = null;
            SetDownloadState(false);
        }
    }

    private void CancelMapDownloadButton_Click(object sender, RoutedEventArgs e) =>
        mapDownloadCancellation?.Cancel();

    private void SetDownloadState(bool downloading)
    {
        DownloadMapButton.IsEnabled = !downloading;
        CancelMapDownloadButton.IsEnabled = downloading;
        MapDownloadUrlTextBox.IsEnabled = !downloading;
        MapDownloadProgressBar.Visibility = downloading || MapDownloadProgressBar.Value > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string GetSuggestedFileName(Uri sourceUri)
    {
        string fileName = Path.GetFileName(Uri.UnescapeDataString(sourceUri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(fileName) ||
            !fileName.EndsWith(".mbtiles", StringComparison.OrdinalIgnoreCase))
            return "offline-map.mbtiles";
        foreach (char invalid in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(invalid, '_');
        return fileName;
    }

    private void UpdateValidationStatus()
    {
        if (string.IsNullOrWhiteSpace(options.MbTilesPath))
        {
            MapValidationText.Text = TryFindResource("Status_MbTilesNotSelected") as string ?? "No MBTiles file selected.";
            return;
        }
        try
        {
            using var provider = new MbTilesTileProvider(options.MbTilesPath);
            MapValidationText.Text = string.Format(
                TryFindResource("Status_MbTilesValid") as string ?? "{0} / {1} / zoom {2}-{3}",
                provider.Metadata.Name, provider.Metadata.Format.ToUpperInvariant(),
                provider.Metadata.MinimumZoom, provider.Metadata.MaximumZoom);
        }
        catch (Exception exception) when (exception is IOException or SqliteException)
        {
            MapValidationText.Text = string.Format(
                TryFindResource("Status_MbTilesInvalid") as string ?? "Unavailable: {0}", exception.Message);
        }
    }

    private async void ClearMapCacheButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult confirmation = MessageBox.Show(
            TryFindResource("Confirm_ClearMapCache") as string ?? "Clear all automatically cached map tiles?",
            TryFindResource("Title_MapCache") as string ?? "Map cache",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes) return;

        try
        {
            using var cache = new GeoMapTileCache(GeoMapTileCache.GetDefaultPath());
            await cache.ClearAsync();
            await UpdateCacheStatusAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            MessageBox.Show(exception.Message,
                TryFindResource("Title_MapCache") as string ?? "Map cache",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task UpdateCacheStatusAsync()
    {
        try
        {
            using var cache = new GeoMapTileCache(GeoMapTileCache.GetDefaultPath());
            GeoMapTileCacheStatistics statistics = await cache.GetStatisticsAsync();
            MapCacheStatusText.Text = string.Format(
                TryFindResource("Status_MapCache") as string ?? "{0:N1} MB / {1:N0} tiles (maximum 512 MB)",
                statistics.ContentBytes / 1024d / 1024d, statistics.TileCount);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            MapCacheStatusText.Text = string.Format(
                TryFindResource("Status_MapCacheUnavailable") as string ?? "Unavailable: {0}", exception.Message);
        }
    }
}
