using SRdeck.Models;

namespace SRdeck.Configuration;

public readonly record struct RadioDisplayState(
    int MainSpanHz, int SpectrumBiasAdj, int WaterfallBiasAdj,
    int SpectrumZoomBiasAdj, int WaterfallZoomBiasAdj);

/// <summary>
/// Owns the mutable last-state document and per-device remembered gains.
/// Live updates run on the UI thread; shutdown saves run after UI subscriptions end.
/// Persistence notifications remain owned by the settings service.
/// </summary>
public sealed class RadioStatePersistence
{
    private readonly ILastStateService _service;

    public LastState State { get; private set; }
    public int SdrPlayRfGainDb { get; set; }
    public int RtlSdrRfGainDb { get; set; }
    public int HackRfRfGainDb { get; set; }
    public int Rx888RfGainDb { get; set; }

    public RadioStatePersistence(ILastStateService service)
    {
        _service = service;
        State = service.LoadLastState();
        SdrPlayRfGainDb = State.SdrPlayRfGainDb;
        RtlSdrRfGainDb = State.RtlSdrRfGainDb;
        HackRfRfGainDb = State.HackRfRfGainDb;
        Rx888RfGainDb = State.Rx888RfGainDb;
    }

    public void Save() => _service.SaveLastState(State);
    public void Backup() => _service.BackupLastState();

    public void Reset()
    {
        State = new LastState();
        // Keep the current hardware gain memories, as the receiver may still be active.
        Save();
    }

    public void SaveFftBatchMode(int mode)
    {
        State.FftBatchMode = mode;
        Save();
    }

    public void SaveFrequencyDisplayMode(string pluginId, FrequencyDisplayMode mode)
    {
        State.PluginFrequencyDisplayModes ??= new();
        State.PluginFrequencyDisplayModes[pluginId] = mode;
        Save();
    }

    public FrequencyDisplayMode GetFrequencyDisplayMode(string pluginId) =>
        State.PluginFrequencyDisplayModes?.TryGetValue(pluginId, out var mode) == true
            ? mode : FrequencyDisplayMode.None;

    public void SaveRadioState(RadioControl control, int sdrPlaySensitivity, RadioDisplayState display)
    {
        CopyTuningState(control);
        State.SdrPlayRfGainDb = SdrPlayRfGainDb;
        State.RtlSdrRfGainDb = RtlSdrRfGainDb;
        State.HackRfRfGainDb = HackRfRfGainDb;
        State.Rx888RfGainDb = Rx888RfGainDb;
        State.SdrPlaySensitivity = sdrPlaySensitivity;
        State.IsR1Visible = control.IsR1Visible;
        State.IsPowerOn = control.IsPowerOn;
        State.IsSpeakerOn = control.IsSpeakerOn;
        State.IsSquelchOn = control.IsSquelchOn;
        State.SquelchDb = control.SquelchDb;
        State.IsZoomWindowVisible = control.IsZoomWindowVisible;
        State.SpanHz = control.SpanHz;
        State.MainSpanHz = display.MainSpanHz;
        State.SpectrumBiasAdj = display.SpectrumBiasAdj;
        State.WaterfallBiasAdj = display.WaterfallBiasAdj;
        State.SpectrumZoomBiasAdj = display.SpectrumZoomBiasAdj;
        State.WaterfallZoomBiasAdj = display.WaterfallZoomBiasAdj;
        Save();
    }

    public void SaveShutdownState(RadioControl control)
    {
        CopyTuningState(control);
        Save();
    }

    private void CopyTuningState(RadioControl control)
    {
        State.CenterFreqHz = control.CenterFreqHz;
        State.TunedFreqHz = control.TunedFreqHz;
        State.DemodMode = control.DemodMode;
        State.StepHz = control.StepHz;
        State.WaterfallColorMode = control.WaterfallColorMode;
    }
}
