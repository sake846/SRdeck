using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using SRdeckPlugin.Wpf;

namespace SRdeck.Views;

public partial class MapCacheInspectorWindow : Window
{
    private IReadOnlyList<GeoMapTileCacheEntry> entries = [];
    private GeoMapViewport? overviewViewport;
    private GeoMapViewport? detailViewport;
    private bool updating;
    private bool loading;
    private bool reloadRequested;
    private bool updatingSelection;
    private bool detailZoomInitialized;

    public MapCacheInspectorWindow()
    {
        InitializeComponent();
        OverviewMap.CacheInspectorMode = true;
        DetailMap.CacheInspectorMode = true;
        OverviewMap.ViewportChanged += (_, view) => OnOverviewViewportChanged(view);
        OverviewMap.CacheTileSelected += (_, tile) => SelectTileFromMap(tile);
        OverviewMap.InspectorLocationSelected += (_, location) =>
        {
            if (DeleteModeToggleButton.IsChecked != true) _ = NavigateDetailAsync(location);
        };
        DetailMap.ViewportChanged += (_, view) =>
        {
            detailViewport = view;
            SyncTargetZoomFromDetail(view.Zoom);
            UpdateZoomStatus();
            _ = UpdateDetailExtentAsync();
        };
        DetailMap.TilesLoaded += (_, _) => { if (OnlineToggleButton.IsChecked == true) _ = LoadInventoryAsync(); };
        Loaded += async (_, _) => await LoadInventoryAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowTheme.ApplyDarkTitleBar(this);
    }

    private string Resource(string key, string fallback) => TryFindResource(key) as string ?? fallback;

    private async Task LoadInventoryAsync()
    {
        if (loading) { reloadRequested = true; return; }
        loading = true;
        try
        {
            using var cache = new GeoMapTileCache(GeoMapTileCache.GetDefaultPath());
            entries = await cache.ListTilesAsync(OnlineGeoMapTileProvider.ProviderId);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            updating = true;
            var counts = entries.GroupBy(tile => tile.Zoom).ToDictionary(group => group.Key, group => group.ToArray());
            int selectedZoom = (ZoomComboBox.SelectedItem as ZoomOption)?.Zoom ??
                (counts.Count > 0 ? counts.MaxBy(pair => pair.Value.Length).Key :
                    ToTileZoom(overviewViewport?.Zoom ?? GeoMapStateStore.GetState(OverviewMap.MapId).Zoom));
            selectedZoom = Math.Clamp(selectedZoom, 1, 19);
            ZoomOption[] options = Enumerable.Range(1, 19)
                .Select(zoom => new ZoomOption(zoom,
                    string.Format(Resource("Status_MapCacheZoom", "Zoom {0}: {1:N0} tiles, {2:N0} expired"),
                        zoom, counts.GetValueOrDefault(zoom)?.Length ?? 0,
                        counts.GetValueOrDefault(zoom)?.Count(tile => tile.ExpiresUtc <= now) ?? 0)))
                .ToArray();
            ZoomComboBox.ItemsSource = options;
            ZoomComboBox.SelectedItem = options[selectedZoom - 1];
            updating = false;
            SummaryText.Text = string.Format(Resource("Status_MapCacheInspector", "{0:N0} tiles / {1:N1} MB / {2:N0} expired"),
                entries.Count, entries.Sum(tile => tile.ContentBytes) / 1024d / 1024d,
                entries.Count(tile => tile.ExpiresUtc <= now));
            ApplyFilter();
            if (!detailZoomInitialized)
            {
                detailZoomInitialized = true;
                await DetailMap.SetInspectorZoomAsync(selectedZoom);
            }
            StatusText.Text = entries.Count == 0
                ? Resource("Status_MapCacheEmpty", "No cached tiles yet. Enable online display and move the map to add tiles.")
                : string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            loading = false;
            if (reloadRequested)
            {
                reloadRequested = false;
                _ = LoadInventoryAsync();
            }
        }
    }

    private void ZoomComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating || !IsLoaded) return;
        ApplyFilter();
        if (ZoomComboBox.SelectedItem is ZoomOption option)
            _ = DetailMap.SetInspectorZoomAsync(option.Zoom);
    }

    private void SyncTargetZoomFromDetail(double zoom)
    {
        if (ZoomComboBox.ItemsSource is not ZoomOption[] options) return;
        int targetZoom = ToTileZoom(zoom);
        if ((ZoomComboBox.SelectedItem as ZoomOption)?.Zoom == targetZoom) return;
        updating = true;
        ZoomComboBox.SelectedItem = options[targetZoom - 1];
        updating = false;
        ApplyFilter();
    }

    private void OnOverviewViewportChanged(GeoMapViewport view)
    {
        overviewViewport = view;
        _ = UpdateCoverageAsync();
        _ = HighlightSelectionAsync();
        _ = UpdateDetailExtentAsync();
        UpdateZoomStatus();
    }

    private async Task UpdateDetailExtentAsync()
    {
        if (detailViewport is not { } view) return;
        try { await OverviewMap.ShowInspectorViewportAsync(view); }
        catch (InvalidOperationException) { }
    }

    private async Task NavigateDetailAsync(GeoMapLocation location)
    {
        if (ZoomComboBox.SelectedItem is not ZoomOption option) return;
        try { await DetailMap.NavigateInspectorToAsync(location.Latitude, location.Longitude, option.Zoom); }
        catch (InvalidOperationException exception) { StatusText.Text = exception.Message; }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!updating && IsLoaded) ApplyFilter();
    }

    private static int ToTileZoom(double zoom) =>
        Math.Clamp((int)Math.Round(zoom, MidpointRounding.AwayFromZero), 1, 19);

    private void ApplyFilter()
    {
        int? zoom = (ZoomComboBox.SelectedItem as ZoomOption)?.Zoom;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var selected = TilesGrid.SelectedItems.Cast<TileRow>()
            .Select(row => (row.Tile.Zoom, row.Tile.X, row.Tile.Y)).ToHashSet();
        TileRow[] rows = zoom is null ? [] : entries
            .Where(tile => tile.Zoom == zoom && (ExpiredOnlyToggleButton.IsChecked != true || tile.ExpiresUtc <= now))
            .Select(tile => new TileRow(tile, now,
                Resource("Status_MapCacheExpired", "Expired"))).ToArray();
        updatingSelection = true;
        TilesGrid.ItemsSource = rows;
        foreach (TileRow row in rows)
            if (selected.Contains((row.Tile.Zoom, row.Tile.X, row.Tile.Y)))
                TilesGrid.SelectedItems.Add(row);
        updatingSelection = false;
        UpdateSelectionStatus();
        NoTilesText.Text = entries.Any(tile => tile.Zoom == zoom)
            ? Resource("Status_MapCacheNoMatches", "No tiles match this filter.")
            : Resource("Status_MapCacheNoZoom", "No cached tiles at this zoom. Each zoom level has separate map tiles.");
        NoTilesText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateZoomStatus();
        _ = HighlightSelectionAsync();
        _ = UpdateCoverageAsync();
    }

    private void UpdateZoomStatus()
    {
        int listZoom = (ZoomComboBox.SelectedItem as ZoomOption)?.Zoom ?? 0;
        int mapZoom = ToTileZoom(overviewViewport?.Zoom ?? 5);
        int rightZoom = ToTileZoom(detailViewport?.Zoom ?? listZoom);
        OverviewZoomText.Text = string.Format(Resource("Status_MapCacheExactZoom",
            "Overview Z{0} / Cache Z{1} · exact tiles"), mapZoom, listZoom);
        DetailZoomText.Text = string.Format(Resource("Status_MapCacheDetailZoom",
            "Display map Z{0}"), rightZoom);
    }

    private async void SelectTileFromMap(GeoMapTileCoordinate tile)
    {
        if (TilesGrid.ItemsSource is not TileRow[] rows) return;
        TileRow? row = rows.FirstOrDefault(item => item.Tile.Zoom == tile.Zoom &&
            item.Tile.X == tile.X && item.Tile.Y == tile.Y);
        if (row is null) return;
        if (DeleteModeToggleButton.IsChecked == true)
        {
            if (TilesGrid.SelectedItems.Contains(row)) TilesGrid.SelectedItems.Remove(row);
            else TilesGrid.SelectedItems.Add(row);
            TilesGrid.ScrollIntoView(row);
            return;
        }
        TilesGrid.SelectedItem = row;
        TilesGrid.ScrollIntoView(row);
        try { await DetailMap.NavigateToTileAsync(tile.Zoom, tile.X, tile.Y); }
        catch (InvalidOperationException exception) { StatusText.Text = exception.Message; }
    }

    private void DeleteMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool deleting = DeleteModeToggleButton.IsChecked == true;
        TilesGrid.SelectedItems.Clear();
        DeleteModeHintText.Visibility = deleting ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionStatus();
    }

    private void TilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingSelection || !IsLoaded) return;
        UpdateSelectionStatus();
        _ = HighlightSelectionAsync();
    }

    private void UpdateSelectionStatus()
    {
        int total = (TilesGrid.ItemsSource as TileRow[])?.Length ?? 0;
        int selected = TilesGrid.SelectedItems.Count;
        SelectionText.Text = selected switch
        {
            0 => string.Format(Resource("Status_MapCacheSelectionNone", "Tiles {0:N0} · none selected"), total),
            1 => string.Format(Resource("Status_MapCacheSelectionOne", "Tiles {0:N0} · selected: {1}"),
                total, (TilesGrid.SelectedItem as TileRow)?.Location),
            _ => string.Format(Resource("Status_MapCacheSelectionMany", "Tiles {0:N0} · {1:N0} selected"),
                total, selected)
        };
        ShowSelectedButton.IsEnabled = DeleteModeToggleButton.IsChecked != true && selected > 0;
        DeleteSelectedButton.IsEnabled = DeleteModeToggleButton.IsChecked == true && selected > 0;
    }

    private async Task HighlightSelectionAsync()
    {
        GeoMapTileCoordinate[] selected = TilesGrid.SelectedItems.Cast<TileRow>()
            .Take(2000).Select(row => new GeoMapTileCoordinate(row.Tile.Zoom, row.Tile.X, row.Tile.Y)).ToArray();
        try { await OverviewMap.HighlightCacheTilesAsync(selected); }
        catch (InvalidOperationException) { }
    }

    private async Task UpdateCoverageAsync()
    {
        if (overviewViewport is not { } view) return;
        if (ZoomComboBox.SelectedItem is not ZoomOption option)
        {
            await OverviewMap.ShowCacheCoverageAsync([]);
            return;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GeoMapTileCacheEntry[] visible = entries.Where(tile => tile.Zoom == option.Zoom && Intersects(tile, view) &&
                (ExpiredOnlyToggleButton.IsChecked != true || tile.ExpiresUtc <= now))
            .ToArray();
        GeoMapCoverageTile[] tiles = visible.Take(2000)
            .Select(tile => new GeoMapCoverageTile(tile.Zoom, tile.X, tile.Y, tile.ExpiresUtc <= now))
            .ToArray();
        try
        {
            await OverviewMap.ShowCacheCoverageAsync(tiles);
            if (visible.Length > 2000)
                StatusText.Text = Resource("Status_MapCacheZoomIn", "Zoom in to see all cached tiles in this area.");
            else if (StatusText.Text == Resource("Status_MapCacheZoomIn", "Zoom in to see all cached tiles in this area."))
                StatusText.Text = string.Empty;
        }
        catch (InvalidOperationException) { }
    }

    private static bool Intersects(GeoMapTileCacheEntry tile, GeoMapViewport view)
    {
        double dimension = Math.Pow(2, tile.Zoom);
        double west = tile.X / dimension * 360 - 180;
        double east = (tile.X + 1) / dimension * 360 - 180;
        static double Latitude(double y, double n) => Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y / n))) * 180 / Math.PI;
        double north = Latitude(tile.Y, dimension);
        double south = Latitude(tile.Y + 1, dimension);
        return north >= view.South && south <= view.North &&
            ((east >= view.West && west <= view.East) ||
             (east + 360 >= view.West && west + 360 <= view.East) ||
             (east - 360 >= view.West && west - 360 <= view.East));
    }

    private async void ShowSelected_Click(object sender, RoutedEventArgs e)
    {
        if (TilesGrid.SelectedItem is not TileRow row) return;
        try { await DetailMap.NavigateToTileAsync(row.Tile.Zoom, row.Tile.X, row.Tile.Y); }
        catch (InvalidOperationException exception) { StatusText.Text = exception.Message; }
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (DeleteModeToggleButton.IsChecked != true) return;
        GeoMapTileCacheEntry[] selected = TilesGrid.SelectedItems.Cast<TileRow>().Select(row => row.Tile).ToArray();
        if (selected.Length == 0) return;
        string message = string.Format(Resource("Confirm_MapCacheDelete", "Delete {0:N0} selected cached tiles?"), selected.Length);
        if (MessageBox.Show(this, message, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            using var cache = new GeoMapTileCache(GeoMapTileCache.GetDefaultPath());
            int deleted = await cache.DeleteTilesAsync(selected);
            await LoadInventoryAsync();
            StatusText.Text = string.Format(Resource("Status_MapCacheDeleted", "Deleted {0:N0} tiles."), deleted);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async void ReloadList_Click(object sender, RoutedEventArgs e) => await LoadInventoryAsync();
    private void Online_Changed(object sender, RoutedEventArgs e)
    {
        bool online = OnlineToggleButton.IsChecked == true;
        DetailMap.InspectorAllowNetwork = online;
        RefreshVisibleButton.IsEnabled = online;
    }

    private async void RefreshVisible_Click(object sender, RoutedEventArgs e)
    {
        if (!DetailMap.IsMapReady)
        {
            StatusText.Text = Resource("Status_MapCacheMapLoading", "The map is still loading.");
            return;
        }
        StatusText.Text = Resource("Status_MapCacheRefreshing", "Refreshing visible tiles...");
        try { await DetailMap.RefreshVisibleTilesAsync(); }
        catch (InvalidOperationException exception) { StatusText.Text = exception.Message; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record ZoomOption(int Zoom, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class TileRow(GeoMapTileCacheEntry tile, DateTimeOffset now, string expiredText)
    {
        public GeoMapTileCacheEntry Tile { get; } = tile;
        public string Coordinate => $"{Tile.X}/{Tile.Y}";
        public string Location
        {
            get
            {
                double n = Math.Pow(2, Tile.Zoom);
                double latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (Tile.Y + .5) / n))) * 180 / Math.PI;
                double longitude = (Tile.X + .5) / n * 360 - 180;
                return string.Format(CultureInfo.InvariantCulture, "{0:F5}, {1:F5}", latitude, longitude);
            }
        }
        public string Age => (now - Tile.StoredUtc).TotalDays >= 1
            ? $"{(int)(now - Tile.StoredUtc).TotalDays} d"
            : $"{Math.Max(0, (int)(now - Tile.StoredUtc).TotalHours)} h";
        public string Expiry => Tile.ExpiresUtc <= now ? expiredText : Tile.ExpiresUtc.ToLocalTime().ToString("MM/dd HH:mm");
    }
}
