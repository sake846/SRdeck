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

    private string? _selectedPluginId;
    [ObservableProperty] private bool _isPluginSelectionBusy;

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

    private void ApplyActiveWaterfallDisplayRequest()
    {
        WaterfallDisplayRequest request = new();
        if (_pluginManager.TryGetActiveCapability<IWaterfallDisplayProvider>(
                out IWaterfallDisplayProvider? provider) && provider is not null)
        {
            request = provider.WaterfallDisplayRequest ?? new WaterfallDisplayRequest();
        }

        WaterfallDisplayTimeMode = Enum.IsDefined(request.TimeMode)
            ? request.TimeMode
            : WaterfallTimeMode.ThreeMinutes;
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

    private void QueueActiveDisplayRestoreAfterRetune() =>
        Interlocked.Exchange(ref _restoreActiveDisplayAfterRetune, 1);

    private void RestoreActiveDisplayAfterRetune()
    {
        if (Interlocked.Exchange(ref _restoreActiveDisplayAfterRetune, 0) == 0) return;
        ApplyActiveWaterfallDisplayRequest();
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
