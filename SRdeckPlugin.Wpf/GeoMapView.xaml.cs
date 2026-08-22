using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SRdeckPlugin.Wpf;

public sealed record GeoMapPoint(double Latitude, double Longitude, string? Color = null);

public sealed record GeoMapMarker(string Id, double Latitude, double Longitude, string Label,
    string Details, string Color = "#88cc00", double? HeadingDegrees = null,
    IReadOnlyList<GeoMapPoint>? Trail = null, string Symbol = "dot",
    bool ShowFlightStateLegend = false, bool IsSelected = false);

public partial class GeoMapView : UserControl
{
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
    private readonly DispatcherTimer updateTimer;

    public GeoMapView()
    {
        InitializeComponent();
        ApplyMapBackground();
        updateTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        updateTimer.Tick += async (_, _) => { updateTimer.Stop(); await UpdateMarkersAsync(); };
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
        AttachCollection(ItemsSource as INotifyCollectionChanged);
        if (mapReady || initializing) { ScheduleUpdate(); return; }
        initializing = true;
        try
        {
            await MapWebView.EnsureCoreWebView2Async();
            GeoMapWebViewSecurity.Configure(MapWebView.CoreWebView2);
            MapWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            MapWebView.NavigationCompleted += OnNavigationCompleted;
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
        }
        catch { }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) { updateTimer.Stop(); AttachCollection(null); }
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
        string mapStatusCss = $"#map-status{{position:absolute;inset:0;z-index:2000;display:flex;align-items:center;justify-content:center;box-sizing:border-box;padding:18px;background:{GetThemeCss("PanelBaseBrush", 18, 18, 18)};color:{GetThemeCss("TextDimBrush", 184, 184, 184)};font:12px sans-serif;text-align:center;pointer-events:none}}#map-status.hidden{{display:none}}";

        return MapHtml
            .Replace("__INIT_LAT__", latStr, StringComparison.Ordinal)
            .Replace("__INIT_LNG__", lngStr, StringComparison.Ordinal)
            .Replace("__INIT_ZOOM__", zoomStr, StringComparison.Ordinal)
            .Replace("<label><input id=\"toggle-trails\" type=\"checkbox\">航跡</label>", ShowTrailToggle ? "<label><input id=\"toggle-trails\" type=\"checkbox\">航跡</label>" : string.Empty, StringComparison.Ordinal)
            .Replace("trailToggle.checked=trailsVisible;", "if(trailToggle)trailToggle.checked=trailsVisible;", StringComparison.Ordinal)
            .Replace("trailToggle.addEventListener('change',e=>{trailsVisible=e.target.checked;trailLines.forEach(x=>x.setStyle({opacity:trailsVisible?x.options.visibleOpacity:0}))});", "if(trailToggle)trailToggle.addEventListener('change',e=>{trailsVisible=e.target.checked;trailLines.forEach(x=>x.setStyle({opacity:trailsVisible?x.options.visibleOpacity:0}))});", StringComparison.Ordinal)
            .Replace("__LEGEND_HTML__", LegendHtml, StringComparison.Ordinal)
            .Replace("__HAS_CONFIGURED_LEGEND__", (!string.IsNullOrWhiteSpace(LegendHtml)).ToString().ToLowerInvariant(), StringComparison.Ordinal)
            .Replace("__USE_CALLOUTS__", UseCalloutLabels.ToString().ToLowerInvariant(), StringComparison.Ordinal)
            .Replace("__MAP_LABEL__", LabelToggleText, StringComparison.Ordinal)
            .Replace("const hasAircraft=points.some(p=>p.Symbol==='aircraft');", "const hasCallsignLabels=points.some(p=>p.Symbol==='aircraft'||p.Symbol==='station'||p.Symbol==='vessel');", StringComparison.Ordinal)
            .Replace("if(hasAircraft&&!displayControl._map)displayControl.addTo(map);else if(!hasAircraft&&displayControl._map)displayControl.remove();", "if(hasCallsignLabels&&!displayControl._map)displayControl.addTo(map);else if(!hasCallsignLabels&&displayControl._map)displayControl.remove();", StringComparison.Ordinal)
            .Replace("className:'',html:html", "className:p.IsSelected?'selected-map-marker':'',html:html", StringComparison.Ordinal)
            .Replace("zIndexOffset:aircraft?500:vessel?400:station?250:0", "zIndexOffset:p.IsSelected?1250:aircraft?500:vessel?400:station?250:0", StringComparison.Ordinal)
            .Replace("className:permanent?'aircraft-label':''", "className:(permanent?'aircraft-label':'')+(p.IsSelected?' selected-map-label':'')", StringComparison.Ordinal)
            .Replace(".bindPopup('<b>'+esc(p.Label)+'</b><br>'+esc(p.Details)+'<br>'+p.Latitude.toFixed(5)+', '+p.Longitude.toFixed(5)).addTo(layer)", ".bindPopup('<b>'+esc(p.Label)+'</b><br>'+esc(p.Details)+'<br>'+p.Latitude.toFixed(5)+', '+p.Longitude.toFixed(5)).on('click',()=>invokeMarker(p.Id)).addTo(layer)", StringComparison.Ordinal)
            .Replace("</style></head>", CalloutCss + "</style></head>", StringComparison.Ordinal)
            .Replace("__PANEL_BACKGROUND__", GetThemeCss("PanelBaseBrush", 18, 18, 18), StringComparison.Ordinal)
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
            .Replace("__OVERLAY_86__", GetThemeCssRgba("PanelBaseBrush", 18, 18, 18, 0.86), StringComparison.Ordinal)
            .Replace("<body>", $"<body><style>{mapStatusCss}</style>", StringComparison.Ordinal);
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

    private const string CalloutCss = """
.leaflet-tooltip.geo-callout:before{display:none!important}
.leaflet-tooltip.geo-callout{margin:0!important;background:__PANEL_SURFACE__;border:1.5px solid __CONTROL_BORDER__;border-radius:5px;box-shadow:0 2px 6px rgba(0,0,0,.5);color:__TEXT_PRIMARY__;font:600 11px sans-serif;padding:3px 7px;white-space:nowrap;cursor:pointer!important;pointer-events:auto!important;opacity:.95}
.leaflet-tooltip.geo-callout *{cursor:pointer!important}
.leaflet-tooltip.geo-callout.selected-map-label{border-color:__FOCUS__;box-shadow:0 0 8px __FOCUS__}
.geo-callout-badge{display:inline-block;width:8px;height:8px;border-radius:50%;margin-right:5px;vertical-align:middle}
.hide-aircraft-labels .geo-callout-leader{display:none}
""";

    private const string MapHtml = """
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline' https://unpkg.com; script-src 'unsafe-inline' https://unpkg.com; img-src data: https://unpkg.com https://tile.openstreetmap.org; connect-src https://tile.openstreetmap.org">
<link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css" integrity="sha256-p4NxAoJBhIIN+hmNHrzRCf9tD/miZyoHS5obTRR9BMY=" crossorigin=""><style>html,body,#map{height:100%;margin:0;background:__PANEL_BACKGROUND__}.leaflet-container{font:12px sans-serif}.geo-marker{width:14px;height:14px;border-radius:50%;border:2px solid __PANEL_SURFACE__;box-shadow:0 0 5px __PANEL_BASE__}.aircraft-marker{width:30px;height:30px;filter:drop-shadow(0 1px 3px __PANEL_BASE__)}.aircraft-marker svg,.station-marker svg,.vessel-marker svg{display:block;overflow:visible}.station-marker{width:26px;height:26px;filter:drop-shadow(0 1px 3px __PANEL_BASE__)}.vessel-marker{width:18px;height:24px;filter:drop-shadow(0 1px 3px __PANEL_BASE__)}.selected-map-marker{filter:drop-shadow(0 0 5px __FOCUS__)}.leaflet-tooltip.aircraft-label{background:__OVERLAY_90__;border:1px solid __CONTROL_BORDER__;border-radius:3px;box-shadow:0 1px 4px __PANEL_BASE__;color:__TEXT_PRIMARY__;font:600 12px Consolas,monospace;padding:2px 5px;white-space:nowrap}.leaflet-tooltip.selected-map-label{border-color:__FOCUS__;box-shadow:0 0 6px __FOCUS__}.leaflet-tooltip-right.aircraft-label:before{border-right-color:__CONTROL_BORDER__}.hide-aircraft-labels .aircraft-label{display:none}.display-control{background:__OVERLAY_92__;border:1px solid __CONTROL_BORDER__;border-radius:4px;box-shadow:0 1px 5px __PANEL_BASE__;color:__TEXT_SECONDARY__;padding:6px 9px;line-height:20px;user-select:none}.display-control label{display:block;cursor:pointer;white-space:nowrap}.display-control input{margin:0 6px 0 0;vertical-align:-1px;accent-color:__CHECKBOX_ACCENT__}.map-legend{background:__OVERLAY_86__;color:__TEXT_SECONDARY__;padding:6px 8px;border:1px solid __CONTROL_BORDER__;border-radius:3px;line-height:18px}.map-legend i{display:inline-block;width:9px;height:9px;margin-right:5px;border-radius:50%}</style></head>
<body><div id="map"></div><div id="map-status">地図を読み込んでいます…</div><script>window.setMapStatus=function(message){const status=document.getElementById('map-status');if(!status)return;status.textContent=message;status.classList.toggle('hidden',!message)};window.invalidateMapSize=function(){};window.addEventListener('error',()=>window.setMapStatus('地図スクリプトでエラーが発生しました。ネットワーク接続を確認してください。'));window.addEventListener('unhandledrejection',()=>window.setMapStatus('地図スクリプトでエラーが発生しました。ネットワーク接続を確認してください。'));setTimeout(()=>{if(window.L===undefined)window.setMapStatus('地図ライブラリを読み込めませんでした。ネットワーク接続を確認してください。')},8000);</script><script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js" integrity="sha256-20nQCchB9co0qIjJZRGuk2/Z9VM+kNiyxNV1lvTlZBo=" crossorigin=""></script><script>
const map=L.map('map',{zoomControl:true}).setView([__INIT_LAT__,__INIT_LNG__],__INIT_ZOOM__);let tileErrors=0;const tiles=L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png',{maxZoom:19,attribution:'&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener noreferrer">OpenStreetMap</a> contributors (ODbL 1.0)'}).on('tileerror',()=>{tileErrors++;window.setMapStatus('地図タイルを読み込めません。ネットワーク接続を確認してください。')}).on('tileload',()=>{if(tileErrors===0)window.setMapStatus('')}).addTo(map);window.invalidateMapSize=()=>map.invalidateSize({pan:false});let layer=L.layerGroup().addTo(map);let trailLines=[];let trailsVisible=true;let labelsVisible=true;const legend=L.control({position:'bottomright'});legend.onAdd=()=>{const d=L.DomUtil.create('div','map-legend');d.innerHTML='__LEGEND_HTML__';return d};const displayControl=L.control({position:'topright'});displayControl.onAdd=()=>{const d=L.DomUtil.create('div','display-control');d.innerHTML='<label><input id="toggle-trails" type="checkbox">航跡</label><label><input id="toggle-labels" type="checkbox">__MAP_LABEL__</label>';const trailToggle=d.querySelector('#toggle-trails');const labelToggle=d.querySelector('#toggle-labels');trailToggle.checked=trailsVisible;labelToggle.checked=labelsVisible;L.DomEvent.disableClickPropagation(d);L.DomEvent.disableScrollPropagation(d);trailToggle.addEventListener('change',e=>{trailsVisible=e.target.checked;trailLines.forEach(x=>x.setStyle({opacity:trailsVisible?x.options.visibleOpacity:0}))});labelToggle.addEventListener('change',e=>{labelsVisible=e.target.checked;map.getContainer().classList.toggle('hide-aircraft-labels',!labelsVisible)});return d};
function postMapState(){const c=map.getCenter();const z=map.getZoom();if(window.chrome&&window.chrome.webview){window.chrome.webview.postMessage(JSON.stringify({type:'mapState',lat:c.lat,lng:c.lng,zoom:z}))}}
function invokeMarker(id){if(window.chrome&&window.chrome.webview){window.chrome.webview.postMessage(JSON.stringify({type:'markerInvoked',id:String(id)}))}}
map.on('moveend zoomend',postMapState);
window.updateMarkers=function(points){layer.clearLayers();trailLines=[];if(!points||points.length===0){if(legend._map)legend.remove();if(displayControl._map)displayControl.remove();return}const hasAircraft=points.some(p=>p.Symbol==='aircraft');const showFlightStateLegend=points.some(p=>p.ShowFlightStateLegend)||__HAS_CONFIGURED_LEGEND__;if(showFlightStateLegend&&!legend._map)legend.addTo(map);else if(!showFlightStateLegend&&legend._map)legend.remove();if(hasAircraft&&!displayControl._map)displayControl.addTo(map);else if(!hasAircraft&&displayControl._map)displayControl.remove();points.forEach(p=>{const ll=[p.Latitude,p.Longitude];if(p.Trail&&p.Trail.length>1){trailRuns(p.Trail,p.Color).forEach(run=>{const outline=L.polyline(run.points,{color:'__PANEL_BASE__',weight:4,opacity:trailsVisible?0.82:0,visibleOpacity:0.82,lineJoin:'round',lineCap:'round',interactive:false}).addTo(layer);const line=L.polyline(run.points,{color:run.color,weight:2,opacity:trailsVisible?1:0,visibleOpacity:1,lineJoin:'round',lineCap:'round',interactive:false}).addTo(layer);trailLines.push(outline,line)})}const aircraft=p.Symbol==='aircraft';const vessel=p.Symbol==='vessel';const station=p.Symbol==='station';const permanent=aircraft||station||vessel;const heading=Number.isFinite(p.HeadingDegrees)?p.HeadingDegrees:0;const html=aircraft?aircraftSvg(p.Color,heading):vessel?vesselSvg(p.Color,heading):station?stationSvg(p.Color):'<div class="geo-marker" style="background:'+safeColor(p.Color)+'"></div>';const size=aircraft?[30,30]:vessel?[18,24]:station?[26,26]:[18,18];const anchor=aircraft?[15,15]:vessel?[9,12]:station?[13,13]:[9,9];const icon=L.divIcon({className:'',html:html,iconSize:size,iconAnchor:anchor});L.marker(ll,{icon,zIndexOffset:aircraft?500:vessel?400:station?250:0}).bindTooltip(esc(p.Label),{direction:'right',offset:aircraft?[12,0]:vessel?[9,0]:station?[10,0]:[7,0],permanent,className:permanent?'aircraft-label':''}).bindPopup('<b>'+esc(p.Label)+'</b><br>'+esc(p.Details)+'<br>'+p.Latitude.toFixed(5)+', '+p.Longitude.toFixed(5)).addTo(layer)});map.getContainer().classList.toggle('hide-aircraft-labels',!labelsVisible);};function trailRuns(trail,fallback){const runs=[];let color=safeColor(trail[0].Color||fallback);let points=[[trail[0].Latitude,trail[0].Longitude]];for(let i=1;i<trail.length;i++){const segmentColor=safeColor(trail[i-1].Color||fallback);if(segmentColor!==color){if(points.length>1)runs.push({color,points});points=[[trail[i-1].Latitude,trail[i-1].Longitude]];color=segmentColor}points.push([trail[i].Latitude,trail[i].Longitude])}if(points.length>1)runs.push({color,points});return runs}function stationSvg(color){color=safeColor(color);return '<div class="station-marker"><svg viewBox="0 0 26 26" width="26" height="26" aria-hidden="true"><path d="M13 4v18M9 22h8M10.5 10.5 13 4l2.5 6.5M8 8a7 7 0 0 0 0 9M18 8a7 7 0 0 1 0 9M5.5 5a11 11 0 0 0 0 15M20.5 5a11 11 0 0 1 0 15" fill="none" stroke="'+color+'" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg></div>'}function vesselSvg(color,heading){color=safeColor(color);return '<div class="vessel-marker" style="transform:rotate('+heading+'deg)"><svg viewBox="0 0 16 24" width="16" height="24" aria-hidden="true"><path d="M8 0.5 15 21.5 8 17.5 1 21.5Z" fill="'+color+'" stroke="__PANEL_BASE__" stroke-width="1.1" stroke-linejoin="round"/><path d="M8 5v9" fill="none" stroke="__PANEL_SURFACE__" stroke-width="1" stroke-linecap="round"/></svg></div>'}function aircraftSvg(color,heading){color=safeColor(color);return '<div class="aircraft-marker" style="transform:rotate('+heading+'deg)"><svg viewBox="0 0 30 30" width="30" height="30" aria-hidden="true"><path d="M15 1.8c1.3 0 2.2 1.5 2.2 3.2v6.1l9.6 6.2v2.8l-9.6-3.2v6.2l3.2 2.2v2.1L15 26l-5.4 1.4v-2.1l3.2-2.2v-6.2l-9.6 3.2v-2.8l9.6-6.2V5c0-1.7.9-3.2 2.2-3.2z" fill="'+color+'" stroke="__PANEL_BASE__" stroke-width="1.4" stroke-linejoin="round"/></svg></div>'}function safeColor(v){return /^#[0-9a-f]{6}$/i.test(v||'')?v:'__SERIES_4__'}function esc(v){const d=document.createElement('div');d.textContent=v||'';return d.innerHTML;}
const useCalloutLabels=__USE_CALLOUTS__;
const calloutLeaderLayer=L.layerGroup().addTo(map);
let calloutEntries=[];
const calloutSlots=[{dir:'right',dx:60,dy:-25},{dir:'left',dx:-60,dy:-25},{dir:'right',dx:60,dy:25},{dir:'left',dx:-60,dy:25},{dir:'right',dx:90,dy:-55},{dir:'left',dx:-90,dy:-55},{dir:'right',dx:90,dy:55},{dir:'left',dx:-90,dy:55}];
function layoutGeoCallouts(){
 calloutLeaderLayer.clearLayers();
 if(!useCalloutLabels||calloutEntries.length===0)return;
 const items=calloutEntries.map(entry=>({entry,pt:map.latLngToLayerPoint(entry.marker.getLatLng())}));
 const clusters=[];const clusterDistanceSquared=140*140;
 items.forEach(item=>{let cluster=clusters.find(candidate=>candidate.some(other=>{const dx=item.pt.x-other.pt.x;const dy=item.pt.y-other.pt.y;return dx*dx+dy*dy<clusterDistanceSquared}));if(cluster)cluster.push(item);else clusters.push([item])});
 clusters.forEach(cluster=>{if(cluster.length===1){bindGeoCallout(cluster[0].entry,'top',[0,-12]);drawGeoCalloutLeader(cluster[0].entry,cluster[0].pt,0,-12);return}cluster.sort((a,b)=>a.pt.y-b.pt.y);cluster.forEach((item,index)=>{const slot=calloutSlots[index%calloutSlots.length];const tier=Math.floor(index/calloutSlots.length);const dx=slot.dx+tier*30*(slot.dx>0?1:-1);const dy=slot.dy+tier*30*(slot.dy>0?1:-1);bindGeoCallout(item.entry,slot.dir,[dx,dy]);drawGeoCalloutLeader(item.entry,item.pt,dx,dy)})});
}
function drawGeoCalloutLeader(entry,markerPoint,offsetX,offsetY){
 const target=L.point(markerPoint.x+offsetX,markerPoint.y+offsetY);const dx=target.x-markerPoint.x;const dy=target.y-markerPoint.y;const distance=Math.sqrt(dx*dx+dy*dy);if(distance===0)return;const end=map.layerPointToLatLng(L.point(target.x+(dx/distance)*4,target.y+(dy/distance)*4));const outer=entry.point.IsSelected?'__FOCUS__':'__CONTROL_BORDER__';
 L.polyline([entry.marker.getLatLng(),end],{color:outer,weight:4,opacity:.95,interactive:false,className:'geo-callout-leader'}).addTo(calloutLeaderLayer);
 L.polyline([entry.marker.getLatLng(),end],{color:'__PANEL_SURFACE__',weight:2,opacity:1,interactive:false,className:'geo-callout-leader'}).addTo(calloutLeaderLayer);
}
function bindGeoCallout(entry,direction,offset){
 const className='aircraft-label geo-callout'+(entry.point.IsSelected?' selected-map-label':'');const tooltip=entry.marker.getTooltip();
 if(entry.direction===direction&&entry.offset&&entry.offset[0]===offset[0]&&entry.offset[1]===offset[1]&&tooltip)return;
 entry.direction=direction;entry.offset=offset;if(tooltip)entry.marker.unbindTooltip();
 entry.marker.bindTooltip(L.tooltip({permanent:true,direction,offset,className,interactive:true}).setContent(entry.content));
 setTimeout(()=>{const element=entry.marker.getTooltip()?.getElement();if(!element||element._hasGeoCalloutEvents)return;element._hasGeoCalloutEvents=true;L.DomEvent.disableClickPropagation(element);L.DomEvent.disableScrollPropagation(element);L.DomEvent.on(element,'click',event=>{L.DomEvent.stopPropagation(event);invokeMarker(entry.point.Id);setTimeout(()=>entry.marker.openPopup(),10)})},0);
}
const updateGeoMarkers=window.updateMarkers;
window.updateMarkers=function(points){
 updateGeoMarkers(points);
 calloutLeaderLayer.clearLayers();calloutEntries=[];
 if(!useCalloutLabels||!points||points.length===0)return;
 const markers=[];layer.eachLayer(candidate=>{if(candidate instanceof L.Marker)markers.push(candidate)});
 calloutEntries=points.map((point,index)=>({point,marker:markers[index],content:'<span class="geo-callout-badge" style="background:'+safeColor(point.Color)+'"></span>'+esc(point.Label),direction:null,offset:null})).filter(entry=>entry.marker);
 layoutGeoCallouts();requestAnimationFrame(layoutGeoCallouts);
};
map.on('zoomend moveend viewreset',layoutGeoCallouts);
</script></body></html>
""";
}
