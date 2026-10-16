using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace SRdeckPlugin.Wpf;

public sealed record GeoMapPoint(double Latitude, double Longitude, string? Color = null);

public sealed record GeoMapMarker(string Id, double Latitude, double Longitude, string Label,
    string Details, string Color = "#88cc00", double? HeadingDegrees = null,
    IReadOnlyList<GeoMapPoint>? Trail = null, string Symbol = "dot",
    bool ShowFlightStateLegend = false, bool IsSelected = false);
public sealed record GeoMapViewport(double West, double South, double East, double North, double Zoom);
public sealed record GeoMapCoverageTile(int Zoom, int X, int Y, bool Expired);
public sealed record GeoMapTileCoordinate(int Zoom, int X, int Y);
public sealed record GeoMapLocation(double Latitude, double Longitude);

public partial class GeoMapView : UserControl
{
    private static readonly Lazy<OnlineGeoMapTileProvider> OnlineTiles = new(CreateOnlineTileProvider);

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(GeoMapView),
        new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty MapIdProperty = DependencyProperty.Register(
        nameof(MapId), typeof(string), typeof(GeoMapView),
        new PropertyMetadata("default_map"));

    public static readonly DependencyProperty LegendHtmlProperty = DependencyProperty.Register(
        nameof(LegendHtml), typeof(string), typeof(GeoMapView),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ShowTrailToggleProperty = DependencyProperty.Register(
        nameof(ShowTrailToggle), typeof(bool), typeof(GeoMapView),
        new PropertyMetadata(true));

    public static readonly DependencyProperty LabelToggleTextProperty = DependencyProperty.Register(
        nameof(LabelToggleText), typeof(string), typeof(GeoMapView),
        new PropertyMetadata("コールサイン"));

    public static readonly DependencyProperty MarkerInvokedCommandProperty = DependencyProperty.Register(
        nameof(MarkerInvokedCommand), typeof(ICommand), typeof(GeoMapView),
        new PropertyMetadata(null));

    public static readonly DependencyProperty UseCalloutLabelsProperty = DependencyProperty.Register(
        nameof(UseCalloutLabels), typeof(bool), typeof(GeoMapView),
        new PropertyMetadata(false));

    private INotifyCollectionChanged? observedCollection;
    private bool mapReady;
    private bool initializing;
    private bool coreEventsAttached;
    private GeoMapSourceOptions sourceOptions = new();
    private bool autoOffline = !NetworkInterface.GetIsNetworkAvailable();
    private readonly DispatcherTimer updateTimer;
    private bool inspectorAllowNetwork;
    private (int Zoom, int X, int Y)? pendingInspectorTile;
    private GeoMapState? pendingInspectorView;
    private int? pendingInspectorZoom;

    public bool CacheInspectorMode { get; set; }
    public bool IsMapReady => mapReady;
    public bool InspectorAllowNetwork
    {
        get => inspectorAllowNetwork;
        set { if (inspectorAllowNetwork == value) return; inspectorAllowNetwork = value; ReloadMap(); }
    }
    public event EventHandler<GeoMapViewport>? ViewportChanged;
    public event EventHandler<GeoMapTileCoordinate>? CacheTileSelected;
    public event EventHandler<GeoMapLocation>? InspectorLocationSelected;
    public event EventHandler? TilesLoaded;

    public GeoMapView()
    {
        InitializeComponent();
        ApplyMapBackground();
        updateTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        updateTimer.Tick += async (_, _) => { updateTimer.Stop(); await UpdateMarkersAsync(); };
        LoadSourceOptions();
    }

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public string MapId { get => (string)GetValue(MapIdProperty); set => SetValue(MapIdProperty, value); }
    public string LegendHtml { get => (string)GetValue(LegendHtmlProperty); set => SetValue(LegendHtmlProperty, value); }
    public bool ShowTrailToggle { get => (bool)GetValue(ShowTrailToggleProperty); set => SetValue(ShowTrailToggleProperty, value); }
    public string LabelToggleText { get => (string)GetValue(LabelToggleTextProperty); set => SetValue(LabelToggleTextProperty, value); }
    public ICommand? MarkerInvokedCommand { get => (ICommand?)GetValue(MarkerInvokedCommandProperty); set => SetValue(MarkerInvokedCommandProperty, value); }
    public bool UseCalloutLabels { get => (bool)GetValue(UseCalloutLabelsProperty); set => SetValue(UseCalloutLabelsProperty, value); }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        GeoMapSourceStore.Changed -= OnMapSourceStoreChanged;
        GeoMapSourceStore.Changed += OnMapSourceStoreChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        AttachCollection(ItemsSource as INotifyCollectionChanged);
        if (mapReady || initializing) { ScheduleUpdate(); return; }
        initializing = true;
        try
        {
            await MapWebView.EnsureCoreWebView2Async();
            GeoMapWebViewSecurity.Configure(MapWebView.CoreWebView2);
            if (!coreEventsAttached)
            {
                MapWebView.CoreWebView2.WebResourceRequested += CoreWebView2_WebResourceRequested;
                MapWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                MapWebView.NavigationCompleted += OnNavigationCompleted;
                coreEventsAttached = true;
            }
            MapWebView.NavigateToString(BuildMapHtml());
        }
        catch (Exception exception)
        {
            StatusText.Text = $"埋め込み地図を開始できません。\nWebView2 Runtimeを確認してください。\n{exception.Message}";
            initializing = false;
        }
    }

    private void CoreWebView2_WebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string message = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(message)) return;
            using var doc = JsonDocument.Parse(message);
            if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return;
            string? messageType = typeProp.GetString();
            if (messageType == "mapState")
            {
                double lat = doc.RootElement.GetProperty("lat").GetDouble();
                double lng = doc.RootElement.GetProperty("lng").GetDouble();
                double zoom = doc.RootElement.GetProperty("zoom").GetDouble();
                GeoMapStateStore.SaveState(MapId, new GeoMapState(lat, lng, zoom));
            }
            else if (messageType == "markerInvoked" &&
                     doc.RootElement.TryGetProperty("id", out JsonElement idProperty))
            {
                string? markerId = idProperty.GetString();
                if (markerId is not null && MarkerInvokedCommand?.CanExecute(markerId) == true)
                    MarkerInvokedCommand.Execute(markerId);
            }
            else if (messageType == "networkStatus" &&
                     doc.RootElement.TryGetProperty("online", out JsonElement onlineProperty) &&
                     onlineProperty.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                ApplyNetworkAvailability(onlineProperty.GetBoolean());
            }
            else if (messageType == "viewport" && CacheInspectorMode)
            {
                ViewportChanged?.Invoke(this, new GeoMapViewport(
                    doc.RootElement.GetProperty("west").GetDouble(),
                    doc.RootElement.GetProperty("south").GetDouble(),
                    doc.RootElement.GetProperty("east").GetDouble(),
                    doc.RootElement.GetProperty("north").GetDouble(),
                    doc.RootElement.GetProperty("zoom").GetDouble()));
            }
            else if (messageType == "tilesLoaded" && CacheInspectorMode)
                TilesLoaded?.Invoke(this, EventArgs.Empty);
            else if (messageType == "cacheTileSelected" && CacheInspectorMode)
            {
                int zoom = doc.RootElement.GetProperty("zoom").GetInt32();
                int x = doc.RootElement.GetProperty("x").GetInt32();
                int y = doc.RootElement.GetProperty("y").GetInt32();
                if (GeoMapTileContent.IsValidCoordinate(zoom, x, y))
                    CacheTileSelected?.Invoke(this, new GeoMapTileCoordinate(zoom, x, y));
            }
            else if (messageType == "inspectorLocation" && CacheInspectorMode)
            {
                double latitude = doc.RootElement.GetProperty("lat").GetDouble();
                double longitude = doc.RootElement.GetProperty("lng").GetDouble();
                if (double.IsFinite(latitude) && double.IsFinite(longitude))
                    InspectorLocationSelected?.Invoke(this, new GeoMapLocation(latitude, longitude));
            }
            else if (messageType == "onlineTileError" && !CacheInspectorMode && sourceOptions.Mode == GeoMapSourceMode.Auto &&
                     !autoOffline)
            {
                autoOffline = true;
                ReloadMap();
            }
        }
        catch { }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        updateTimer.Stop();
        AttachCollection(null);
        GeoMapSourceStore.Changed -= OnMapSourceStoreChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }
    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    { var view = (GeoMapView)d; view.AttachCollection(e.NewValue as INotifyCollectionChanged); view.ScheduleUpdate(); }
    private void AttachCollection(INotifyCollectionChanged? collection)
    {
        if (observedCollection is not null) observedCollection.CollectionChanged -= OnCollectionChanged;
        observedCollection = collection;
        if (observedCollection is not null) observedCollection.CollectionChanged += OnCollectionChanged;
    }
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleUpdate();
    private void ScheduleUpdate() { if (!updateTimer.IsEnabled) updateTimer.Start(); }

    private void ApplyMapBackground()
    {
        System.Windows.Media.Color color = GetThemeColor("PanelBaseBrush", System.Windows.Media.Color.FromRgb(18, 18, 18));
        MapWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    private string BuildMapHtml()
    {
        GeoMapState state = GeoMapStateStore.GetState(MapId);
        string latStr = state.Latitude.ToString(CultureInfo.InvariantCulture);
        string lngStr = state.Longitude.ToString(CultureInfo.InvariantCulture);
        string zoomStr = state.Zoom.ToString(CultureInfo.InvariantCulture);
        bool cacheOnly = CacheInspectorMode ? !InspectorAllowNetwork :
            GeoMapSourceStore.ShouldUseCacheOnly(sourceOptions, !autoOffline);
        string tileUrl = "https://srdeck-map-tiles.local/{z}/{x}/{y}";
        const int minimumZoom = 1;
        const int maximumZoom = 19;
        const string onlineAttribution = "&copy; <a href=\"https://www.openstreetmap.org/copyright\" target=\"_blank\" rel=\"noopener noreferrer\">OpenStreetMap</a> contributors (ODbL 1.0)";
        string tileFailure = cacheOnly
            ? "この地域または縮尺の地図タイルはキャッシュされていません。"
            : "地図タイルを読み込めません。ネットワーク接続を確認してください。";

        return MapHtml
            .Replace("__INIT_LAT__", latStr, StringComparison.Ordinal)
            .Replace("__INIT_LNG__", lngStr, StringComparison.Ordinal)
            .Replace("__INIT_ZOOM__", zoomStr, StringComparison.Ordinal)
            .Replace("__TILE_URL__", tileUrl, StringComparison.Ordinal)
            .Replace("__MIN_ZOOM__", minimumZoom.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__MAX_ZOOM__", maximumZoom.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__ATTRIBUTION__", JsonSerializer.Serialize(onlineAttribution), StringComparison.Ordinal)
            .Replace("__TILE_FAILURE__", JsonSerializer.Serialize(tileFailure), StringComparison.Ordinal)
            .Replace("__IS_OFFLINE__", cacheOnly.ToString().ToLowerInvariant(), StringComparison.Ordinal)
            .Replace("__INSPECTOR__", CacheInspectorMode.ToString().ToLowerInvariant(), StringComparison.Ordinal)
            .Replace("<label><input id=\"toggle-trails\" type=\"checkbox\">航跡</label>", ShowTrailToggle ? "<label><input id=\"toggle-trails\" type=\"checkbox\">航跡</label>" : string.Empty, StringComparison.Ordinal)
            .Replace("__LEGEND_HTML__", LegendHtml, StringComparison.Ordinal)
            .Replace("__HAS_CONFIGURED_LEGEND__", (!string.IsNullOrWhiteSpace(LegendHtml)).ToString().ToLowerInvariant(), StringComparison.Ordinal)
            .Replace("__USE_CALLOUTS__", UseCalloutLabels.ToString().ToLowerInvariant(), StringComparison.Ordinal)
            .Replace("__MAP_LABEL__", LabelToggleText, StringComparison.Ordinal)
            .Replace("__PANEL_BASE__", GetThemeCss("PanelBaseBrush", 18, 18, 18), StringComparison.Ordinal)
            .Replace("__PANEL_SURFACE__", GetThemeCss("PanelSurfaceBrush", 30, 30, 30), StringComparison.Ordinal)
            .Replace("__CONTROL_BORDER__", GetThemeCss("ControlBorderBrush", 136, 136, 136), StringComparison.Ordinal)
            .Replace("__FOCUS__", GetThemeCss("FocusBorderBrush", 85, 200, 216), StringComparison.Ordinal)
            .Replace("__TEXT_PRIMARY__", GetThemeCss("TextPrimaryBrush", 242, 242, 242), StringComparison.Ordinal)
            .Replace("__TEXT_SECONDARY__", GetThemeCss("TextSecondaryBrush", 214, 214, 214), StringComparison.Ordinal)
            .Replace("__TEXT_DIM__", GetThemeCss("TextDimBrush", 184, 184, 184), StringComparison.Ordinal)
            .Replace("__CHECKBOX_ACCENT__", GetThemeCss("LedYellowGreenBrush", 180, 230, 50), StringComparison.Ordinal)
            .Replace("__SERIES_1__", GetThemeCss("PluginDataSeries1Brush", 77, 208, 225), StringComparison.Ordinal)
            .Replace("__SERIES_3__", GetThemeCss("PluginDataSeries3Brush", 255, 183, 77), StringComparison.Ordinal)
            .Replace("__SERIES_4__", GetThemeCss("PluginDataSeries4Brush", 38, 166, 154), StringComparison.Ordinal)
            .Replace("__OVERLAY_90__", GetThemeCssRgba("PanelBaseBrush", 18, 18, 18, 0.90), StringComparison.Ordinal)
            .Replace("__OVERLAY_92__", GetThemeCssRgba("PanelBaseBrush", 18, 18, 18, 0.92), StringComparison.Ordinal)
            .Replace("__OVERLAY_86__", GetThemeCssRgba("PanelBaseBrush", 18, 18, 18, 0.86), StringComparison.Ordinal);
    }

    private string GetThemeCss(string resourceKey, byte fallbackRed, byte fallbackGreen, byte fallbackBlue)
    {
        System.Windows.Media.Color color = GetThemeColor(
            resourceKey, System.Windows.Media.Color.FromRgb(fallbackRed, fallbackGreen, fallbackBlue));
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private string GetThemeCssRgba(
        string resourceKey, byte fallbackRed, byte fallbackGreen, byte fallbackBlue, double opacity)
    {
        System.Windows.Media.Color color = GetThemeColor(
            resourceKey, System.Windows.Media.Color.FromRgb(fallbackRed, fallbackGreen, fallbackBlue));
        return FormattableString.Invariant($"rgba({color.R},{color.G},{color.B},{opacity:0.##})");
    }

    private System.Windows.Media.Color GetThemeColor(string resourceKey, System.Windows.Media.Color fallback) =>
        (TryFindResource(resourceKey) as SolidColorBrush)?.Color ?? fallback;

    private async void OnNavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        initializing = false;
        if (!e.IsSuccess)
        {
            StatusText.Text = "地図ページを読み込めませんでした。ネットワーク接続を確認してください。";
            await SetMapStatusAsync(StatusText.Text);
            return;
        }
        string ready = await MapWebView.ExecuteScriptAsync("typeof L !== 'undefined' && typeof window.updateMarkers === 'function'");
        if (!string.Equals(ready, "true", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "地図ライブラリを読み込めませんでした。ネットワーク接続を確認してください。";
            await SetMapStatusAsync(StatusText.Text);
            return;
        }
        mapReady = true; StatusOverlay.Visibility = Visibility.Collapsed;
        await InvalidateMapSizeAsync();
        await UpdateMarkersAsync();
        if (pendingInspectorTile is { } tile)
        {
            pendingInspectorTile = null;
            await NavigateToTileAsync(tile.Zoom, tile.X, tile.Y);
        }
        else if (pendingInspectorView is { } view)
        {
            pendingInspectorView = null;
            await NavigateInspectorToAsync(view.Latitude, view.Longitude, (int)view.Zoom);
        }
        else if (pendingInspectorZoom is int zoom)
        {
            pendingInspectorZoom = null;
            await SetInspectorZoomAsync(zoom);
        }
        if (CacheInspectorMode)
            await MapWebView.ExecuteScriptAsync("window.reportViewport && window.reportViewport();");
    }

    private void OnMapSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0) _ = InvalidateMapSizeAsync();
    }

    private async Task InvalidateMapSizeAsync()
    {
        if (!mapReady || MapWebView.CoreWebView2 is null) return;
        try { await MapWebView.ExecuteScriptAsync("window.invalidateMapSize && window.invalidateMapSize();"); }
        catch (InvalidOperationException) { }
    }

    private async Task SetMapStatusAsync(string message)
    {
        if (MapWebView.CoreWebView2 is null) return;
        try
        {
            await MapWebView.ExecuteScriptAsync(
                $"window.setMapStatus && window.setMapStatus({JsonSerializer.Serialize(message)});");
        }
        catch (InvalidOperationException) { }
    }

    private async Task UpdateMarkersAsync()
    {
        if (!mapReady || MapWebView.CoreWebView2 is null) return;
        GeoMapMarker[] markers = ItemsSource?.Cast<object>().OfType<GeoMapMarker>().ToArray() ?? [];
        try { await MapWebView.ExecuteScriptAsync($"window.updateMarkers({JsonSerializer.Serialize(markers)});"); }
        catch (InvalidOperationException) { }
    }

    public async Task NavigateToTileAsync(int zoom, int x, int y)
    {
        if (!CacheInspectorMode) return;
        pendingInspectorZoom = null;
        pendingInspectorView = null;
        if (!mapReady || MapWebView.CoreWebView2 is null)
        {
            pendingInspectorTile = (zoom, x, y);
            return;
        }
        await MapWebView.ExecuteScriptAsync($"window.inspectTile({zoom},{x},{y});");
    }

    public async Task NavigateInspectorToAsync(double latitude, double longitude, int zoom)
    {
        if (!CacheInspectorMode || !double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            zoom is < 1 or > 19) return;
        latitude = Math.Clamp(latitude, -85, 85);
        longitude = ((longitude + 180) % 360 + 360) % 360 - 180;
        pendingInspectorTile = null;
        pendingInspectorZoom = null;
        if (!mapReady || MapWebView.CoreWebView2 is null)
        {
            pendingInspectorView = new(latitude, longitude, zoom);
            return;
        }
        await MapWebView.ExecuteScriptAsync(
            $"window.setInspectorView({JsonSerializer.Serialize(latitude)},{JsonSerializer.Serialize(longitude)},{zoom});");
    }

    public async Task SetInspectorZoomAsync(int zoom)
    {
        if (!CacheInspectorMode || zoom is < 1 or > 19) return;
        pendingInspectorTile = null;
        pendingInspectorView = null;
        if (!mapReady || MapWebView.CoreWebView2 is null)
        {
            pendingInspectorZoom = zoom;
            return;
        }
        await MapWebView.ExecuteScriptAsync($"window.setInspectorZoom({zoom});");
    }

    public async Task ShowCacheCoverageAsync(IReadOnlyList<GeoMapCoverageTile> tiles)
    {
        if (!mapReady || MapWebView.CoreWebView2 is null) return;
        await MapWebView.ExecuteScriptAsync(
            $"window.showCacheCoverage({JsonSerializer.Serialize(tiles)});");
    }

    public async Task HighlightCacheTilesAsync(IReadOnlyList<GeoMapTileCoordinate> tiles)
    {
        if (!mapReady || MapWebView.CoreWebView2 is null) return;
        await MapWebView.ExecuteScriptAsync($"window.highlightCacheTiles({JsonSerializer.Serialize(tiles)});");
    }

    public async Task ShowInspectorViewportAsync(GeoMapViewport view)
    {
        if (!mapReady || MapWebView.CoreWebView2 is null) return;
        await MapWebView.ExecuteScriptAsync(
            $"window.showInspectorViewport({JsonSerializer.Serialize(view)});");
    }

    public async Task RefreshVisibleTilesAsync()
    {
        if (!mapReady || MapWebView.CoreWebView2 is null || !InspectorAllowNetwork) return;
        await MapWebView.ExecuteScriptAsync("window.refreshMapTiles();");
    }

    private void LoadSourceOptions()
    {
        sourceOptions = GeoMapSourceStore.GetOptions();
        autoOffline = !NetworkInterface.GetIsNetworkAvailable();
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ApplyNetworkAvailability(e.IsAvailable));
            return;
        }
        ApplyNetworkAvailability(e.IsAvailable);
    }

    private void ApplyNetworkAvailability(bool isOnline)
    {
        if (CacheInspectorMode) return;
        if (sourceOptions.Mode == GeoMapSourceMode.Offline) return;
        bool nextAutoOffline = !isOnline;
        if (autoOffline == nextAutoOffline) return;
        autoOffline = nextAutoOffline;
        ReloadMap();
    }

    private void OnMapSourceStoreChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnMapSourceStoreChanged(sender, e));
            return;
        }
        LoadSourceOptions();
        ReloadMap();
    }

    private void ReloadMap()
    {
        if (MapWebView.CoreWebView2 is null) return;
        mapReady = false;
        initializing = true;
        StatusText.Text = "地図を切り替えています…";
        StatusOverlay.Visibility = Visibility.Visible;
        MapWebView.NavigateToString(BuildMapHtml());
    }

    private async void CoreWebView2_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (GeoMapAssetProvider.IsAssetUri(e.Request.Uri))
        {
            GeoMapTile? asset = GeoMapAssetProvider.GetAsset(e.Request.Uri);
            e.Response = asset is null
                ? MapWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                    Stream.Null, 404, "Not Found", "Cache-Control: no-store")
                : MapWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(asset.Content, writable: false), 200, "OK",
                    $"Content-Type: {asset.ContentType}\r\nCache-Control: public, max-age=31536000, immutable");
            return;
        }

        bool cacheOnly = CacheInspectorMode ? !InspectorAllowNetwork :
            GeoMapSourceStore.ShouldUseCacheOnly(sourceOptions, !autoOffline);
        bool forceRefresh = CacheInspectorMode && e.Request.Uri.EndsWith("?refresh=1", StringComparison.Ordinal);
        string tileUri = forceRefresh ? e.Request.Uri[..^"?refresh=1".Length] : e.Request.Uri;
        if (!GeoMapTileUri.TryParse(tileUri, out int zoom, out int x, out int y)) return;

        CoreWebView2Deferral deferral = e.GetDeferral();
        try
        {
            OnlineGeoMapTileResult result = await OnlineTiles.Value.GetTileAsync(
                zoom, x, y, allowNetwork: !cacheOnly, CancellationToken.None, forceRefresh);
            GeoMapTile? tile = result.Tile;
            if (result.NetworkFailed && !CacheInspectorMode && sourceOptions.Mode == GeoMapSourceMode.Auto && !autoOffline)
            {
                autoOffline = true;
                _ = Dispatcher.BeginInvoke(ReloadMap, DispatcherPriority.Background);
            }
            if (tile is null)
            {
                e.Response = MapWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                    Stream.Null, 404, "Not Found", "Cache-Control: no-store");
                return;
            }
            e.Response = MapWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(tile.Content, writable: false), 200, "OK",
                $"Content-Type: {tile.ContentType}\r\nCache-Control: no-store");
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
        {
            e.Response = MapWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                Stream.Null, 500, "Tile Error", "Cache-Control: no-store");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static OnlineGeoMapTileProvider CreateOnlineTileProvider()
    {
        GeoMapTileCache? cache = null;
        try
        {
            cache = new GeoMapTileCache(GeoMapTileCache.GetDefaultPath());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
        }
        return new OnlineGeoMapTileProvider(cache);
    }

    private const string MapHtml = """
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline' https://srdeck-map-assets.local; script-src 'unsafe-inline' https://srdeck-map-assets.local; img-src data: https://srdeck-map-assets.local https://srdeck-map-tiles.local; connect-src https://srdeck-map-tiles.local">
<link rel="stylesheet" href="https://srdeck-map-assets.local/leaflet.css"><style>html,body,#map{height:100%;margin:0;background:__PANEL_BASE__}.leaflet-container{font:12px sans-serif}.geo-marker{width:14px;height:14px;border-radius:50%;border:2px solid __PANEL_SURFACE__;box-shadow:0 0 5px __PANEL_BASE__}.aircraft-marker{width:30px;height:30px;filter:drop-shadow(0 1px 3px __PANEL_BASE__)}.aircraft-marker svg,.station-marker svg,.vessel-marker svg{display:block;overflow:visible}.station-marker{width:26px;height:26px;filter:drop-shadow(0 1px 3px __PANEL_BASE__)}.vessel-marker{width:18px;height:24px;filter:drop-shadow(0 1px 3px __PANEL_BASE__)}.selected-map-marker{filter:drop-shadow(0 0 5px __FOCUS__)}.leaflet-tooltip.aircraft-label{background:__OVERLAY_90__;border:1px solid __CONTROL_BORDER__;border-radius:3px;box-shadow:0 1px 4px __PANEL_BASE__;color:__TEXT_PRIMARY__;font:600 12px Consolas,monospace;padding:2px 5px;white-space:nowrap}.leaflet-tooltip.selected-map-label{border-color:__FOCUS__;box-shadow:0 0 6px __FOCUS__}.leaflet-tooltip-right.aircraft-label:before{border-right-color:__CONTROL_BORDER__}.hide-aircraft-labels .aircraft-label{display:none}.display-control{background:__OVERLAY_92__;border:1px solid __CONTROL_BORDER__;border-radius:4px;box-shadow:0 1px 5px __PANEL_BASE__;color:__TEXT_SECONDARY__;padding:6px 9px;line-height:20px;user-select:none}.display-control label{display:block;cursor:pointer;white-space:nowrap}.display-control input{margin:0 6px 0 0;vertical-align:-1px;accent-color:__CHECKBOX_ACCENT__}.map-legend{background:__OVERLAY_86__;color:__TEXT_SECONDARY__;padding:6px 8px;border:1px solid __CONTROL_BORDER__;border-radius:3px;line-height:18px}.map-legend i{display:inline-block;width:9px;height:9px;margin-right:5px;border-radius:50%}.leaflet-tooltip.geo-callout:before{display:none!important}
.leaflet-tooltip.geo-callout{margin:0!important;background:__PANEL_SURFACE__;border:1.5px solid __CONTROL_BORDER__;border-radius:5px;box-shadow:0 2px 6px rgba(0,0,0,.5);color:__TEXT_PRIMARY__;font:600 11px sans-serif;padding:3px 7px;white-space:nowrap;box-sizing:border-box;overflow:hidden;text-overflow:ellipsis;cursor:pointer!important;pointer-events:auto!important;opacity:.95}
.leaflet-tooltip.geo-callout *{cursor:pointer!important}
.leaflet-tooltip.geo-callout.selected-map-label{border-color:__FOCUS__;box-shadow:0 0 8px __FOCUS__}
.geo-callout-badge{display:inline-block;width:8px;height:8px;border-radius:50%;margin-right:5px;vertical-align:middle}
.leaflet-tooltip.geo-callout-group{width:max-content;padding:3px;overflow-y:auto;overflow-x:hidden}
.geo-callout-row{display:flex;align-items:center;gap:5px;width:100%;box-sizing:border-box;background:none;border:0;border-radius:3px;padding:4px 6px;color:inherit;font:inherit;text-align:left}
.geo-callout-row:hover,.geo-callout-row:focus-visible,.geo-callout-row.is-selected{background:__PANEL_BASE__;outline:1px solid __FOCUS__;outline-offset:-1px}
.geo-callout-row .geo-callout-badge{flex:none;margin:0}
.geo-callout-name{min-width:0;white-space:normal;overflow-wrap:anywhere}
.hide-aircraft-labels .geo-callout-leader{display:none}
#map-status{position:absolute;inset:0;z-index:2000;display:flex;align-items:center;justify-content:center;box-sizing:border-box;padding:18px;background:__PANEL_BASE__;color:__TEXT_DIM__;font:12px sans-serif;text-align:center;pointer-events:none}#map-status.hidden{display:none}
</style></head>
<body><div id="map"></div><div id="map-status">地図を読み込んでいます…</div><script>window.setMapStatus=function(message){const status=document.getElementById('map-status');if(!status)return;status.textContent=message;status.classList.toggle('hidden',!message)};window.invalidateMapSize=function(){};window.addEventListener('error',()=>window.setMapStatus('地図スクリプトでエラーが発生しました。'));window.addEventListener('unhandledrejection',()=>window.setMapStatus('地図スクリプトでエラーが発生しました。'));setTimeout(()=>{if(window.L===undefined)window.setMapStatus('地図ライブラリを読み込めませんでした。')},8000);</script><script src="https://srdeck-map-assets.local/leaflet.js"></script><script>
 function postNetworkStatus(online){if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(JSON.stringify({type:'networkStatus',online}))}
 window.addEventListener('online',()=>postNetworkStatus(true));window.addEventListener('offline',()=>postNetworkStatus(false));
 const isOffline=__IS_OFFLINE__,isInspector=__INSPECTOR__;const map=L.map('map',{zoomControl:true}).setView([__INIT_LAT__,__INIT_LNG__],__INIT_ZOOM__);
 let tileErrors=0;let hasGoodView=false;let revertingToGoodView=false;let returnTimer=null;
 let lastGoodView={lat:__INIT_LAT__,lng:__INIT_LNG__,zoom:__INIT_ZOOM__};
 const tiles=L.tileLayer('__TILE_URL__',{minZoom:__MIN_ZOOM__,maxZoom:__MAX_ZOOM__,attribution:__ATTRIBUTION__})
  .on('loading',()=>{tileErrors=0})
  .on('tileerror',()=>{
   tileErrors++;
   if(isOffline&&!isInspector&&hasGoodView&&!revertingToGoodView){
    revertingToGoodView=true;window.setMapStatus('この地域または縮尺に有効なキャッシュがないため、直前に表示できた地図へ戻ります…');
    if(returnTimer!==null)clearTimeout(returnTimer);
    returnTimer=setTimeout(()=>{
     map.setView([lastGoodView.lat,lastGoodView.lng],lastGoodView.zoom,{animate:false});
     setTimeout(()=>{tileErrors=0;revertingToGoodView=false;window.setMapStatus('')},250);
    },1200);
   }else if(!revertingToGoodView&&!isInspector){window.setMapStatus(__TILE_FAILURE__)}
   if(!isOffline&&window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(JSON.stringify({type:'onlineTileError'}));
  })
  .on('load',()=>{
   if(isInspector){window.setMapStatus('');if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(JSON.stringify({type:'tilesLoaded'}));tiles.setUrl('__TILE_URL__',true)}
   if(tileErrors!==0||revertingToGoodView)return;
   commitGoodView();
  }).addTo(map);
 if(isInspector){
  const coverage=L.layerGroup().addTo(map),selection=L.layerGroup().addTo(map),detailExtent=L.layerGroup().addTo(map);
  function bounds(z,x,y){const n=2**z,lat=v=>Math.atan(Math.sinh(Math.PI*(1-2*v/n)))*180/Math.PI;return [[lat(y+1),x/n*360-180],[lat(y),(x+1)/n*360-180]]}
  function postInspectorLocation(latlng){if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(JSON.stringify({type:'inspectorLocation',lat:latlng.lat,lng:latlng.lng}))}
  map.on('click',e=>postInspectorLocation(e.latlng));
  window.showCacheCoverage=items=>{
   coverage.clearLayers();
   (items||[]).forEach(t=>L.rectangle(bounds(t.Zoom,t.X,t.Y),{color:t.Expired?'#ffae42':'#88cc00',weight:1,fillOpacity:.25,interactive:true,bubblingMouseEvents:false})
    .bindTooltip('Z'+t.Zoom+' · '+t.X+'/'+t.Y)
    .on('click',()=>{if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(JSON.stringify({type:'cacheTileSelected',zoom:t.Zoom,x:t.X,y:t.Y}))})
    .addTo(coverage));
  };
  window.highlightCacheTiles=items=>{
   selection.clearLayers();
   (items||[]).forEach(t=>{
    const b=bounds(t.Zoom,t.X,t.Y);
    if(t.Zoom>map.getZoom()+3)L.circleMarker([(b[0][0]+b[1][0])/2,(b[0][1]+b[1][1])/2],{radius:7,color:'#55c8d8',weight:3,fillOpacity:.6,interactive:false}).addTo(selection);
    else L.rectangle(b,{color:'#55c8d8',weight:3,fillOpacity:.12,interactive:false}).addTo(selection);
   });
  };
  window.showInspectorViewport=v=>{
   detailExtent.clearLayers();
   L.rectangle([[v.South,v.West],[v.North,v.East]],{color:'#ff66cc',weight:2,fillOpacity:0,interactive:false,dashArray:'5,4'}).addTo(detailExtent);
   L.circleMarker([(v.South+v.North)/2,(v.West+v.East)/2],{radius:6,color:'#ff66cc',weight:2,fillColor:'#ff66cc',fillOpacity:.8,interactive:false}).addTo(detailExtent);
  };
  window.inspectTile=(z,x,y)=>{const b=bounds(z,x,y);window.highlightCacheTiles([{Zoom:z,X:x,Y:y}]);map.setView([(b[0][0]+b[1][0])/2,(b[0][1]+b[1][1])/2],z,{animate:false})};
  window.setInspectorView=(lat,lng,z)=>map.setView([lat,lng],z,{animate:false});
  window.setInspectorZoom=z=>map.setZoom(z,{animate:false});
  window.refreshMapTiles=()=>{tiles.setUrl('__TILE_URL__?refresh=1',true);tiles.redraw()};
  function postViewport(){const b=map.getBounds();postMapState();if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(JSON.stringify({type:'viewport',west:b.getWest(),south:b.getSouth(),east:b.getEast(),north:b.getNorth(),zoom:map.getZoom()}))}
  window.reportViewport=postViewport;map.on('moveend',postViewport);
 }
 window.invalidateMapSize=()=>map.invalidateSize({pan:false});let layer=L.layerGroup().addTo(map);let trailLines=[];let trailsVisible=true;let labelsVisible=true;const legend=L.control({position:'bottomright'});legend.onAdd=()=>{const d=L.DomUtil.create('div','map-legend');d.innerHTML='__LEGEND_HTML__';return d};const displayControl=L.control({position:'topright'});displayControl.onAdd=()=>{const d=L.DomUtil.create('div','display-control');d.innerHTML='<label><input id="toggle-trails" type="checkbox">航跡</label><label><input id="toggle-labels" type="checkbox">__MAP_LABEL__</label>';const trailToggle=d.querySelector('#toggle-trails');const labelToggle=d.querySelector('#toggle-labels');if(trailToggle)trailToggle.checked=trailsVisible;labelToggle.checked=labelsVisible;L.DomEvent.disableClickPropagation(d);L.DomEvent.disableScrollPropagation(d);if(trailToggle)trailToggle.addEventListener('change',e=>{trailsVisible=e.target.checked;trailLines.forEach(x=>x.setStyle({opacity:trailsVisible?x.options.visibleOpacity:0}))});labelToggle.addEventListener('change',e=>{labelsVisible=e.target.checked;map.getContainer().classList.toggle('hide-aircraft-labels',!labelsVisible)});return d};
 function postMapState(){const c=map.getCenter();const z=map.getZoom();if(window.chrome&&window.chrome.webview){window.chrome.webview.postMessage(JSON.stringify({type:'mapState',lat:c.lat,lng:c.lng,zoom:z}))}}
 function commitGoodView(){const center=map.getCenter();lastGoodView={lat:center.lat,lng:center.lng,zoom:map.getZoom()};hasGoodView=true;window.setMapStatus('');postMapState()}
 function invokeMarker(id){if(window.chrome&&window.chrome.webview){window.chrome.webview.postMessage(JSON.stringify({type:'markerInvoked',id:String(id)}))}}
map.on('moveend zoomend',()=>{if(!tiles.isLoading()&&tileErrors===0&&!revertingToGoodView)commitGoodView()});
const markerEntries = new Map();
window.updateMarkers=function(points){
 points=points||[];
 const active=new Set(points.map(p=>String(p.Id)));
 for(const [id,entry] of markerEntries){
  if(active.has(id))continue;
  entry.layers.forEach(item=>layer.removeLayer(item));
  markerEntries.delete(id);
 }
 const hasCallsignLabels=points.some(p=>p.Symbol==='aircraft'||p.Symbol==='station'||p.Symbol==='vessel');
 const showFlightStateLegend=points.some(p=>p.ShowFlightStateLegend)||__HAS_CONFIGURED_LEGEND__;
 if(showFlightStateLegend&&points.length&&!legend._map)legend.addTo(map);
 else if((!showFlightStateLegend||!points.length)&&legend._map)legend.remove();
 if(hasCallsignLabels&&!displayControl._map)displayControl.addTo(map);
 else if(!hasCallsignLabels&&displayControl._map)displayControl.remove();
 points.forEach(p=>{
  const id=String(p.Id),signature=JSON.stringify(p),previous=markerEntries.get(id);
  if(previous&&previous.signature===signature)return;
  if(previous)previous.layers.forEach(item=>layer.removeLayer(item));
  const layers=[],lines=[],ll=[p.Latitude,p.Longitude];
  if(p.Trail&&p.Trail.length>1)trailRuns(p.Trail,p.Color).forEach(run=>{
   const outline=L.polyline(run.points,{color:'__PANEL_BASE__',weight:4,opacity:trailsVisible?0.82:0,visibleOpacity:0.82,lineJoin:'round',lineCap:'round',interactive:false}).addTo(layer);
   const line=L.polyline(run.points,{color:run.color,weight:2,opacity:trailsVisible?1:0,visibleOpacity:1,lineJoin:'round',lineCap:'round',interactive:false}).addTo(layer);
   layers.push(outline,line);lines.push(outline,line);
  });
  const aircraft=p.Symbol==='aircraft',vessel=p.Symbol==='vessel',station=p.Symbol==='station';
  const permanent=aircraft||station||vessel,heading=Number.isFinite(p.HeadingDegrees)?p.HeadingDegrees:0;
  const html=aircraft?aircraftSvg(p.Color,heading):vessel?vesselSvg(p.Color,heading):station?stationSvg(p.Color):'<div class="geo-marker" style="background:'+safeColor(p.Color)+'"></div>';
  const size=aircraft?[30,30]:vessel?[18,24]:station?[26,26]:[18,18];
  const anchor=aircraft?[15,15]:vessel?[9,12]:station?[13,13]:[9,9];
  const icon=L.divIcon({className:p.IsSelected?'selected-map-marker':'',html:html,iconSize:size,iconAnchor:anchor});
  const marker=L.marker(ll,{icon,zIndexOffset:p.IsSelected?1250:aircraft?500:vessel?400:station?250:0})
   .bindTooltip(esc(p.Label),{direction:'right',offset:aircraft?[12,0]:vessel?[9,0]:station?[10,0]:[7,0],permanent,className:(permanent?'aircraft-label':'')+(p.IsSelected?' selected-map-label':'')})
   .bindPopup('<b>'+esc(p.Label)+'</b><br>'+esc(p.Details)+'<br>'+p.Latitude.toFixed(5)+', '+p.Longitude.toFixed(5))
   .on('click',()=>invokeMarker(p.Id)).addTo(layer);
  layers.push(marker);
  markerEntries.set(id,{signature,layers,lines,marker});
 });
 trailLines=[...markerEntries.values()].flatMap(entry=>entry.lines);
 map.getContainer().classList.toggle('hide-aircraft-labels',!labelsVisible);
};
function trailRuns(trail,fallback){const runs=[];let color=safeColor(trail[0].Color||fallback);let points=[[trail[0].Latitude,trail[0].Longitude]];for(let i=1;i<trail.length;i++){const segmentColor=safeColor(trail[i-1].Color||fallback);if(segmentColor!==color){if(points.length>1)runs.push({color,points});points=[[trail[i-1].Latitude,trail[i-1].Longitude]];color=segmentColor}points.push([trail[i].Latitude,trail[i].Longitude])}if(points.length>1)runs.push({color,points});return runs}function stationSvg(color){color=safeColor(color);return '<div class="station-marker"><svg viewBox="0 0 26 26" width="26" height="26" aria-hidden="true"><path d="M13 4v18M9 22h8M10.5 10.5 13 4l2.5 6.5M8 8a7 7 0 0 0 0 9M18 8a7 7 0 0 1 0 9M5.5 5a11 11 0 0 0 0 15M20.5 5a11 11 0 0 1 0 15" fill="none" stroke="'+color+'" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg></div>'}function vesselSvg(color,heading){color=safeColor(color);return '<div class="vessel-marker" style="transform:rotate('+heading+'deg)"><svg viewBox="0 0 16 24" width="16" height="24" aria-hidden="true"><path d="M8 0.5 15 21.5 8 17.5 1 21.5Z" fill="'+color+'" stroke="__PANEL_BASE__" stroke-width="1.1" stroke-linejoin="round"/><path d="M8 5v9" fill="none" stroke="__PANEL_SURFACE__" stroke-width="1" stroke-linecap="round"/></svg></div>'}function aircraftSvg(color,heading){color=safeColor(color);return '<div class="aircraft-marker" style="transform:rotate('+heading+'deg)"><svg viewBox="0 0 30 30" width="30" height="30" aria-hidden="true"><path d="M15 1.8c1.3 0 2.2 1.5 2.2 3.2v6.1l9.6 6.2v2.8l-9.6-3.2v6.2l3.2 2.2v2.1L15 26l-5.4 1.4v-2.1l3.2-2.2v-6.2l-9.6 3.2v-2.8l9.6-6.2V5c0-1.7.9-3.2 2.2-3.2z" fill="'+color+'" stroke="__PANEL_BASE__" stroke-width="1.4" stroke-linejoin="round"/></svg></div>'}function safeColor(v){return /^#[0-9a-f]{6}$/i.test(v||'')?v:'__SERIES_4__'}function esc(v){const d=document.createElement('div');d.textContent=v||'';return d.innerHTML;}
const useCalloutLabels=__USE_CALLOUTS__;
const calloutLeaderLayer=L.layerGroup().addTo(map);
const calloutLabelLayer=L.layerGroup().addTo(map);
let calloutEntries=[];
function layoutGeoCallouts(){
 calloutLeaderLayer.clearLayers();calloutLabelLayer.clearLayers();
 if(!useCalloutLabels||!labelsVisible||calloutEntries.length===0)return;
 const size=map.getSize(),container=map.getContainer().getBoundingClientRect(),items=[];
 if(size.x<32||size.y<32)return;
 calloutEntries.forEach(entry=>{
  const pt=map.latLngToContainerPoint(entry.marker.getLatLng());
  if(pt.x<0||pt.y<0||pt.x>size.x||pt.y>size.y)return;
  items.push({entry,pt});
 });
 const occupied=items.map(item=>({left:item.pt.x-14,right:item.pt.x+14,top:item.pt.y-14,bottom:item.pt.y+14}));
 map.getContainer().querySelectorAll('.leaflet-control').forEach(control=>{
  const rect=control.getBoundingClientRect();
  occupied.push({left:rect.left-container.left,right:rect.right-container.left,top:rect.top-container.top,bottom:rect.bottom-container.top});
 });
 const groups=clusterGeoCallouts(items).map(members=>{
  members.sort((a,b)=>a.entry.point.Label.localeCompare(b.entry.point.Label)||String(a.entry.point.Id).localeCompare(String(b.entry.point.Id)));
  const pt={x:members.reduce((sum,item)=>sum+item.pt.x,0)/members.length,y:members.reduce((sum,item)=>sum+item.pt.y,0)/members.length};
  const clearance=Math.max(...members.map(item=>Math.abs(item.pt.x-pt.x)))+28;
  return {members,pt,clearance,selected:members.some(item=>item.entry.point.IsSelected)};
 });
 groups.sort((a,b)=>Number(b.selected)-Number(a.selected)||a.pt.y-b.pt.y||a.pt.x-b.pt.x);
 // ponytail: scan visible rectangles; use a spatial index if dense maps make layout slow.
 groups.forEach(group=>{
  const grouped=group.members.length>1;
  const maxWidth=grouped?Math.max(group.pt.x-group.clearance-8,size.x-8-group.pt.x-group.clearance):size.x-16;
  const tooltip=bindGeoCallout(group,Math.max(16,maxWidth),Math.min(240,size.y-16));
  const element=tooltip.getElement();element.style.visibility='hidden';
  const bounds=findGeoCalloutBounds(group.pt,element.offsetWidth,element.offsetHeight,size,occupied,grouped?group.clearance:0);
  if(!bounds){calloutLabelLayer.removeLayer(tooltip);return}
  occupied.push(bounds);
  tooltip.setLatLng(map.containerPointToLatLng(L.point((bounds.left+bounds.right)/2,(bounds.top+bounds.bottom)/2)));
  element.style.visibility='';
  const selected=element.querySelector('.is-selected');
  if(selected)element.scrollTop=Math.max(0,selected.offsetTop-element.clientHeight+selected.offsetHeight);
  drawGeoCalloutLeader(group,bounds);
 });
}
function clusterGeoCallouts(items){
 const pending=[...items],groups=[];
 while(pending.length){
  const members=[pending.shift()];
  for(let index=0;index<members.length;index++){
   for(let i=pending.length-1;i>=0;i--){
    const dx=members[index].pt.x-pending[i].pt.x,dy=members[index].pt.y-pending[i].pt.y;
    if(dx*dx+dy*dy<=36*36)members.push(...pending.splice(i,1));
   }
  }
  groups.push(members);
 }
 return groups;
}
function findGeoCalloutBounds(pt,width,height,size,occupied,clearance=0){
 const gap=6,padding=8,centered=Math.max(padding,Math.min(size.x-padding-width,pt.x-width/2));
 const lefts=clearance?[pt.x+clearance,pt.x-clearance-width]:[centered];
 const step=clearance?30:height+gap,baseTop=Math.max(padding,Math.min(size.y-padding-height,pt.y-height/2));
 for(let row=0;row<Math.ceil(size.y/step);row++){
  const tops=clearance?[baseTop-row*step,baseTop+row*step]:[pt.y-20-height-row*step,pt.y+20+row*step];
  for(const top of tops)for(const left of lefts){
   const bounds={left,right:left+width,top,bottom:top+height};
   if(left<padding||bounds.right>size.x-padding||top<padding||bounds.bottom>size.y-padding)continue;
   if(!occupied.some(other=>bounds.left<other.right+gap&&bounds.right>other.left-gap&&bounds.top<other.bottom+gap&&bounds.bottom>other.top-gap))return bounds;
  }
 }
 return null;
}
function geoCalloutLeaderPoints(pt,bounds,grouped){
 if(grouped){
  const right=bounds.left>pt.x,endX=right?bounds.left:bounds.right,elbowX=endX+(right?-12:12),endY=(bounds.top+bounds.bottom)/2;
  return [[pt.x+(right?14:-14),pt.y],[elbowX,pt.y],[elbowX,endY],[endX,endY]];
 }
 const above=bounds.bottom<pt.y,y=above?bounds.bottom:bounds.top,targetX=Math.max(bounds.left+8,Math.min(bounds.right-8,pt.x));
 return [[pt.x,pt.y+(above?-14:14)],[pt.x,y],[targetX,y]];
}
function drawGeoCalloutLeader(group,bounds){
 const points=geoCalloutLeaderPoints(group.pt,bounds,group.members.length>1).map(point=>map.containerPointToLatLng(L.point(point[0],point[1])));
 const outer=group.selected?'__FOCUS__':'__CONTROL_BORDER__';
 L.polyline(points,{color:outer,weight:3,opacity:.85,interactive:false,className:'geo-callout-leader'}).addTo(calloutLeaderLayer);
 L.polyline(points,{color:'__PANEL_SURFACE__',weight:1,opacity:1,interactive:false,className:'geo-callout-leader'}).addTo(calloutLeaderLayer);
}
function bindGeoCallout(group,maxWidth,maxHeight){
 const content=group.members.map((item,index)=>'<button type="button" class="geo-callout-row'+(item.entry.point.IsSelected?' is-selected':'')+'" data-callout-index="'+index+'"><span class="geo-callout-badge" style="background:'+safeColor(item.entry.point.Color)+'"></span><span class="geo-callout-name">'+esc(item.entry.point.Label)+'</span></button>').join('');
 const tooltip=L.tooltip({permanent:true,direction:'center',className:'aircraft-label geo-callout geo-callout-group',interactive:true}).setLatLng(map.containerPointToLatLng(L.point(group.pt.x,group.pt.y))).setContent(content).addTo(calloutLabelLayer);
 const element=tooltip.getElement();element.style.maxWidth=Math.floor(maxWidth)+'px';element.style.maxHeight=maxHeight+'px';
 element.querySelectorAll('.geo-callout-row').forEach((row,index)=>row.title=group.members[index].entry.point.Label);
 L.DomEvent.disableClickPropagation(element);L.DomEvent.disableScrollPropagation(element);
 L.DomEvent.on(element,'click',event=>{const row=event.target.closest('.geo-callout-row');if(!row)return;L.DomEvent.stopPropagation(event);const entry=group.members[Number(row.dataset.calloutIndex)].entry;invokeMarker(entry.point.Id);setTimeout(()=>entry.marker.openPopup(),10)});
 return tooltip;
}
const updateGeoMarkers=window.updateMarkers;
window.updateMarkers=function(points){
 updateGeoMarkers(points);
 calloutLeaderLayer.clearLayers();calloutLabelLayer.clearLayers();calloutEntries=[];
 if(!useCalloutLabels||!points||points.length===0)return;
 calloutEntries=points.map(point=>({point,marker:markerEntries.get(String(point.Id))?.marker})).filter(entry=>entry.marker);
 calloutEntries.forEach(entry=>{entry.marker.unbindTooltip();entry.marker.getElement().title=entry.point.Label});
 layoutGeoCallouts();
};
map.on('zoomend moveend viewreset resize',layoutGeoCallouts);
map.getContainer().addEventListener('change',event=>{if(event.target.id==='toggle-labels')layoutGeoCallouts()});
</script></body></html>
""";
}
