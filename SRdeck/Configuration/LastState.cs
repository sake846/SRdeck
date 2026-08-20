using SRdeck.Models;

namespace SRdeck.Configuration;

public class LastState
{
    public FrequencyDisplayMode FrequencyDisplayMode { get; set; } = FrequencyDisplayMode.Both;
    public int FftBatchMode { get; set; } = 0;
    public DemodWaveMode DemodWaveDisplayMode { get; set; } = DemodWaveMode.Wave;

    // --- State Persistence ---
    public int TunedFreqHz { get; set; } = 80000000;
    public int CenterFreqHz { get; set; } = 80000000;
    public DemodulationMode DemodMode { get; set; } = DemodulationMode.FM_Wide;
    public int StepHz { get; set; } = 100000;

    public int SdrPlayRfGainDb { get; set; } = 20;
    public int SdrPlaySensitivity { get; set; } = 50;
    public int RtlSdrRfGainDb { get; set; } = 100;
    public int Rx888RfGainDb { get; set; } = 100;
    public int Rx888SampleRateHz { get; set; } = 32000000;

    public int WaterfallColorMode { get; set; } = 0;

    // --- Extended State ---
    public bool IsR1Visible { get; set; } = true;
    public bool IsPowerOn { get; set; } = false;
    public bool IsSpeakerOn { get; set; } = false;
    public bool IsSquelchOn { get; set; } = false;
    public int SquelchDb { get; set; } = -115;
    public bool IsZoomWindowVisible { get; set; } = false;
    public int SpanHz { get; set; } = 250000;
    public int MainSpanHz { get; set; } = 7000000;

    public int SpectrumBiasAdj { get; set; } = 0;
    public int WaterfallBiasAdj { get; set; } = 0;
    public int SpectrumZoomBiasAdj { get; set; } = 0;
    public int WaterfallZoomBiasAdj { get; set; } = 0;

    public string? ProcessPriority { get; set; }
}
