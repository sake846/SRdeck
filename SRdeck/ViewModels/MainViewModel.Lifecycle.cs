using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using SRdeck.Models;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public async Task ClosingAsync()
    {
        await _pluginManager.ShutdownAsync();
        _lastState.FftBatchMode = FftBatchMode;
        PersistFftBatchState(FftBatchMode);

        RadioControl radioControl = _engine.Control;
        _lastState.TunedFreqHz = radioControl.TunedFreqHz;
        _lastState.CenterFreqHz = radioControl.CenterFreqHz;
        _lastState.DemodMode = radioControl.DemodMode;
        _lastState.StepHz = radioControl.StepHz;

        _lastState.WaterfallColorMode = radioControl.WaterfallColorMode;
        _lastState.DemodWaveDisplayMode = radioControl.DemodWaveDisplayMode;

        _lastState.FrequencyDisplayMode = (IsBandPlanVisible, IsStationNameVisible) switch
        {
            (true, true) => FrequencyDisplayMode.Both,
            (true, false) => FrequencyDisplayMode.BandOnly,
            (false, true) => FrequencyDisplayMode.StationOnly,
            _ => FrequencyDisplayMode.None
        };
        _lastStateService.SaveLastState(_lastState);
    }

    public void OnRendering()
    {
        if (_engine?.NeedsBackgroundRedraw == true) _engine.NeedsBackgroundRedraw = false;
    }
}
