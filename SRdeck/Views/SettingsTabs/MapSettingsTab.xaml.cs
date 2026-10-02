using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using SRdeck.Views;
using SRdeckPlugin.Wpf;

namespace SRdeck.Views.SettingsTabs;

public partial class MapSettingsTab : UserControl
{
    private bool initializing;
    private GeoMapSourceOptions options = new();

    public MapSettingsTab()
    {
        InitializeComponent();
        LoadOptions();
        _ = UpdateCacheStatusAsync();
    }

    private void LoadOptions()
    {
        initializing = true;
        try
        {
            options = GeoMapSourceStore.GetOptions();
            MapModeComboBox.SelectedValue = options.Mode.ToString();
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
        if (!GeoMapSourceStore.TrySaveOptions(options with { Mode = mode }))
        {
            MessageBox.Show(
                TryFindResource("Error_MapSettingsSave") as string ?? "The map settings could not be saved.",
                TryFindResource("Title_MapError") as string ?? "Map settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            LoadOptions();
            return;
        }
        options = options with { Mode = mode };
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

    private async void OpenMapCacheInspector_Click(object sender, RoutedEventArgs e)
    {
        var window = new MapCacheInspectorWindow { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        await UpdateCacheStatusAsync();
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
