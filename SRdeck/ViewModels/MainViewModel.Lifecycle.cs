using CommunityToolkit.Mvvm.ComponentModel;
using SRdeck.Models;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public async Task ClosingAsync()
    {
        await Diagnostics.StopLoadCaptureAsync();

        using (SRdeck.Services.ShutdownDiagnosticLog.Scope("PluginManager.ShutdownAsync"))
        {
            await _pluginManager.ShutdownAsync();
        }

        using (SRdeck.Services.ShutdownDiagnosticLog.Scope("SaveLastStateAndSettings"))
        {
            _lastState.FftBatchMode = FftBatchMode;
            PersistFftBatchState(FftBatchMode);

            RadioControl radioControl = _engine.Control;
            _lastState.TunedFreqHz = radioControl.TunedFreqHz;
            _lastState.CenterFreqHz = radioControl.CenterFreqHz;
            _lastState.DemodMode = radioControl.DemodMode;
            _lastState.StepHz = radioControl.StepHz;

            _lastState.WaterfallColorMode = radioControl.WaterfallColorMode;
            _lastState.DemodWaveDisplayMode = radioControl.DemodWaveDisplayMode;

            _lastStateService.SaveLastState(_lastState);
            await SettingsPersistence.FlushNotificationsAsync();
            SettingsPersistence.Dispose();
        }
    }

    public void OnRendering()
    {
        if (_engine?.NeedsBackgroundRedraw == true) _engine.NeedsBackgroundRedraw = false;
    }
}
