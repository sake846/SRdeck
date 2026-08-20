namespace SRdeck.Messages;

public class ZoomFftDataMessage(int receiverIndex, float[] fftData, float specBias, float wfBias, float bandwidthCorrection = 0f)
{
    public int ReceiverIndex { get; } = receiverIndex;
    public float[] FftData { get; } = fftData;
    public float SpecBias { get; } = specBias;
    public float WfBias { get; } = wfBias;
    public float BandwidthCorrection { get; } = bandwidthCorrection;
}
