using CommunityToolkit.Mvvm.ComponentModel;
using SRdeckPlugin.Contracts;
using SRdeck.Services.Plugins;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Messages;
using SRdeck.Models;
using System.Windows;
using System.Windows.Threading;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SemaphoreSlim _pluginSelectionGate = new(1, 1);
    private bool _isSynchronizingPluginSelection;
    private int _restoreActiveDisplayAfterRetune;
    private int _mainSpanToRestoreAfterRetune;
    private int _waterfallDisplayRequestVersion;
    private int _waterfallDisplayRequestVersionAtRetune;
    private int _waterfallDisplayRequestVersionAtRetuneCompletion;
    private int _lastAppliedWaterfallDisplayWidthHz;
    private IWaterfallDisplayRequestChangedProvider? _waterfallDisplayRequestNotifier;
    private bool _hasInitializedWaterfallDisplayWidth;

    private string? _selectedPluginId;
    [ObservableProperty] private bool _isPluginSelectionBusy;
    [ObservableProperty] private bool _isPluginSettingsExpanded = true;

    public string? SelectedPluginId
    {
        get => _selectedPluginId;
        set
        {
            // ItemsSource is refreshed whenever an plugin runtime changes. WPF briefly writes
            // null to SelectedValue while rebuilding the view; accepting that transient value
            // leaves the MODE combo blank even though the manager still has an active plugin.
            if (string.IsNullOrWhiteSpace(value) && !_isSynchronizingPluginSelection)
            {
                _selectedPluginId = null;
                ReassertPluginSelection();
                return;
            }
            if (!SetProperty(ref _selectedPluginId, value) || _isSynchronizingPluginSelection ||
                string.IsNullOrWhiteSpace(value)) return;
            _ = ApplyPluginSelectionAsync(value);
        }
    }

    public bool IsPluginSelectionEnabled => !IsPluginSelectionBusy;

    public void SyncPluginSelectionFromManager()
    {
        _isSynchronizingPluginSelection = true;
        try
        {
            string? activePluginId = _pluginManager.ActivePluginId;
            if (string.Equals(_selectedPluginId, activePluginId, StringComparison.Ordinal))
                OnPropertyChanged(nameof(SelectedPluginId));
            else
                SelectedPluginId = activePluginId;
        }
        finally
        {
            _isSynchronizingPluginSelection = false;
        }
    }

    private void OnPluginWorkspacePropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PluginWorkspaceViewModel.Plugins))
        {
            ReassertPluginSelection();
            ApplyActiveWaterfallDisplayRequest();
        }
    }

    private void ApplyActiveWaterfallDisplayRequest(bool bandwidthChanged = false)
    {
        if (_waterfallDisplayRequestNotifier is not null)
            _waterfallDisplayRequestNotifier.WaterfallDisplayRequestChanged -=
                OnWaterfallDisplayRequestChanged;
        _waterfallDisplayRequestNotifier = null;

        WaterfallDisplayRequest request = new();
        bool isDynamicDisplayRequest = false;
        if (_pluginManager.TryGetActiveCapability<IWaterfallDisplayProvider>(
                out IWaterfallDisplayProvider? provider) && provider is not null)
        {
            request = provider.WaterfallDisplayRequest ?? new WaterfallDisplayRequest();
            if (provider is IWaterfallDisplayRequestChangedProvider notifier)
            {
                isDynamicDisplayRequest = true;
                _waterfallDisplayRequestNotifier = notifier;
                notifier.WaterfallDisplayRequestChanged += OnWaterfallDisplayRequestChanged;
            }
        }

        WaterfallDisplayTimeMode = Enum.IsDefined(request.TimeMode)
            ? request.TimeMode
            : WaterfallTimeMode.ThreeMinutes;
        bool hasPreferredBandwidth = request.PreferredDisplayBandwidthHz is > 0;
        if (!hasPreferredBandwidth ||
            (isDynamicDisplayRequest && !bandwidthChanged && _hasInitializedWaterfallDisplayWidth))
            return;

        _hasInitializedWaterfallDisplayWidth = true;
        bool prefersZoom = request.PreferredDisplayBandwidthHz is > 0 &&
            request.PreferredDisplayBandwidthHz < Display.BaseMainSpanHz;
        if (!Display.IsMainViewZoomed && prefersZoom)
        {
            CompleteSdrCenterSnapBeforeZoom();
        }

        if (prefersZoom)
        {
            _isApplyingAtomicMainViewUpdate = true;
            try
            {
                Display.ApplyPreferredMainSpanHz(request.PreferredDisplayBandwidthHz);
            }
            finally
            {
                _isApplyingAtomicMainViewUpdate = false;
            }
            CenterPreferredPluginDisplayOnTunedFrequency();
        }
        else
        {
            Display.ApplyPreferredMainSpanHz(request.PreferredDisplayBandwidthHz);
        }
        _lastAppliedWaterfallDisplayWidthHz = Display.CurrentMainSpanHz;
        Volatile.Write(ref _waterfallDisplayRequestVersionAtRetuneCompletion,
            Volatile.Read(ref _waterfallDisplayRequestVersion));
    }

    private void OnWaterfallDisplayRequestChanged(object? sender, EventArgs e)
    {
        Interlocked.Increment(ref _waterfallDisplayRequestVersion);
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            // Process after pending retune/display updates; bandwidth changes then win.
            _ = dispatcher.InvokeAsync(() => ApplyActiveWaterfallDisplayRequest(
                bandwidthChanged: true), DispatcherPriority.Background);
            return;
        }
        ApplyActiveWaterfallDisplayRequest(bandwidthChanged: true);
    }

    private void CenterPreferredPluginDisplayOnTunedFrequency()
    {
        if (!Display.IsMainViewZoomed) return;

        RadioControl radioControl = _engine.Control;
        int tunedFrequencyHz = radioControl.TunedFreqHz;
        if (tunedFrequencyHz <= 0) return;

        radioControl.CenterFreqHz = tunedFrequencyHz;
        radioControl.FreqOffsetHz = 0;
        radioControl.MainSpanHz = Display.CurrentMainSpanHz;
        radioControl.BaseMainSpanHz = Display.BaseMainSpanHz;
        if (radioControl.CursorFreqHz >= 0)
            radioControl.CursorFreqOffsetHz = radioControl.CursorFreqHz - tunedFrequencyHz;
        _engine.Control = radioControl;
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
    }

    private void QueueActiveDisplayRestoreAfterRetune()
    {
        int requestVersion = Volatile.Read(ref _waterfallDisplayRequestVersion);
        bool hasNewDisplayRequest = requestVersion !=
            Volatile.Read(ref _waterfallDisplayRequestVersionAtRetuneCompletion);
        Interlocked.Exchange(ref _mainSpanToRestoreAfterRetune,
            hasNewDisplayRequest
                ? -1
                : Display.IsMainViewZoomed ? Display.CurrentMainSpanHz : 0);
        Interlocked.Exchange(ref _waterfallDisplayRequestVersionAtRetune,
            requestVersion);
        Interlocked.Exchange(ref _restoreActiveDisplayAfterRetune, 1);
    }

    private void RestoreActiveDisplayAfterRetune()
    {
        if (Interlocked.Exchange(ref _restoreActiveDisplayAfterRetune, 0) == 0) return;
        int requestVersionAtRetune = Interlocked.Exchange(
            ref _waterfallDisplayRequestVersionAtRetune, 0);
        int spanHz = Interlocked.Exchange(ref _mainSpanToRestoreAfterRetune, 0);
        if (requestVersionAtRetune != Volatile.Read(ref _waterfallDisplayRequestVersion))
        {
            ApplyActiveWaterfallDisplayRequest(bandwidthChanged: true);
            Volatile.Write(ref _waterfallDisplayRequestVersionAtRetuneCompletion,
                Volatile.Read(ref _waterfallDisplayRequestVersion));
            return;
        }
        if (spanHz < 0)
        {
            ApplyActiveWaterfallDisplayRequest(bandwidthChanged: true);
            Volatile.Write(ref _waterfallDisplayRequestVersionAtRetuneCompletion,
                requestVersionAtRetune);
            return;
        }
        if (spanHz > 0 && spanHz != Volatile.Read(ref _lastAppliedWaterfallDisplayWidthHz))
        {
            CompleteSdrCenterSnapBeforeZoom();
            _isApplyingAtomicMainViewUpdate = true;
            try
            {
                Display.ApplyPreferredMainSpanHz(spanHz);
            }
            finally
            {
                _isApplyingAtomicMainViewUpdate = false;
            }
            CenterPreferredPluginDisplayOnTunedFrequency();
            Volatile.Write(ref _waterfallDisplayRequestVersionAtRetuneCompletion,
                requestVersionAtRetune);
            return;
        }
        ApplyActiveWaterfallDisplayRequest(
            bandwidthChanged: spanHz > 0 || requestVersionAtRetune !=
                Volatile.Read(ref _waterfallDisplayRequestVersion));
        Volatile.Write(ref _waterfallDisplayRequestVersionAtRetuneCompletion,
            Volatile.Read(ref _waterfallDisplayRequestVersion));
    }

    private void ReassertPluginSelection()
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) { SyncPluginSelectionFromManager(); return; }
        _ = dispatcher.InvokeAsync(SyncPluginSelectionFromManager, DispatcherPriority.DataBind);
    }

    partial void OnIsPluginSelectionBusyChanged(bool value) =>
        OnPropertyChanged(nameof(IsPluginSelectionEnabled));

    private async Task ApplyPluginSelectionAsync(string pluginId)
    {
        await _pluginSelectionGate.WaitAsync();
        IsPluginSelectionBusy = true;
        try
        {
            if (!string.Equals(_pluginManager.ActivePluginId, pluginId, StringComparison.Ordinal))
            {
                // Plugin tuning must reach the SDR hardware. While the main spectrum is
                // zoomed, the tuning coordinator intentionally keeps the current SDR center.
                // Reset the display and RadioControl span before activating the next plugin.
                Display.SyncMainZoomSpanHz(0);
            }

            PluginOperationResult activation = await _pluginManager.ActivateAsync(pluginId);
            if (!activation.Succeeded)
            {
                Console.Error.WriteLine(activation.Error);
                SyncPluginSelectionFromManager();
                return;
            }

            if (SdrControl.IsStarted)
            {
                PluginOperationResult start = await _pluginManager.StartStreamAsync();
                if (!start.Succeeded) Console.Error.WriteLine(start.Error);
            }

            ApplyActiveWaterfallDisplayRequest();
            PersistPluginSelection();
            SyncPluginSelectionFromManager();
        }
        finally
        {
            IsPluginSelectionBusy = false;
            _pluginSelectionGate.Release();
        }
    }

    private void PersistPluginSelection()
    {
        _engine.InitialAppSettings.Plugins.SelectedPluginId = _pluginManager.ActivePluginId;
        _settingsService.SaveSettings(_engine.InitialAppSettings);
    }
}
