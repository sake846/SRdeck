namespace SRdeck.Messages;

/// <summary>
/// メインウォーターフォール（ブラウザ表示用）の512-binデータを運ぶメッセージ。
/// </summary>
public class MainWaterfallDataMessage(float[] fftData, int spanHz, int fsHz, int frameSerial, long blockSequence)
{
    public float[] FftData { get; } = fftData;
    public int SpanHz { get; } = spanHz;
    public int FsHz { get; } = fsHz;
    public int FrameSerial { get; } = frameSerial;
    public long BlockSequence { get; } = blockSequence;
}
