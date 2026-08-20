using System.Numerics;

namespace SRdeckCore.SignalProcessing;

/// <summary>Streaming complex-IQ rational resampler with a Blackman-windowed polyphase FIR.</summary>
public sealed class PolyphaseRationalResampler
{
    private readonly int tapsPerPhase;
    private readonly int maximumExactPhases;
    private readonly bool allowUpsampling;
    private float[][] phaseTaps = [];
    private float[][] reversedPhaseTaps = [];
    private float[] historyI;
    private float[] historyQ;
    private int interpolationFactor;
    private int decimationFactor;
    private int historyPosition;
    private long inputIndex;
    private long nextOutputNumerator;

    public PolyphaseRationalResampler(
        int tapsPerPhase = 32,
        int maximumExactPhases = 256,
        bool allowUpsampling = true)
    {
        if (tapsPerPhase < 2) throw new ArgumentOutOfRangeException(nameof(tapsPerPhase));
        if (maximumExactPhases < 1) throw new ArgumentOutOfRangeException(nameof(maximumExactPhases));
        this.tapsPerPhase = tapsPerPhase;
        this.maximumExactPhases = maximumExactPhases;
        this.allowUpsampling = allowUpsampling;
        historyI = new float[tapsPerPhase * 2];
        historyQ = new float[tapsPerPhase * 2];
        ResetState();
    }

    public int InterpolationFactor => interpolationFactor;
    public int DecimationFactor => decimationFactor;
    public int TapsPerPhase => tapsPerPhase;
    public double GroupDelaySamples => (tapsPerPhase - 1) * 0.5;
    public bool UsesSimd => Vector.IsHardwareAccelerated && tapsPerPhase >= Vector<float>.Count;

    public void Configure(int sourceSampleRateHz, int sourceDecimationFactor,
        int outputSampleRateHz, double cutoffHz)
    {
        if (sourceSampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sourceSampleRateHz));
        if (sourceDecimationFactor <= 0) throw new ArgumentOutOfRangeException(nameof(sourceDecimationFactor));
        if (outputSampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(outputSampleRateHz));

        long numerator = checked((long)outputSampleRateHz * sourceDecimationFactor);
        long divisor = GreatestCommonDivisor(numerator, sourceSampleRateHz);
        interpolationFactor = checked((int)(numerator / divisor));
        decimationFactor = checked((int)(sourceSampleRateHz / divisor));
        if (!allowUpsampling && interpolationFactor > decimationFactor)
            throw new InvalidOperationException("This resampler is configured for decimation-only use.");

        double intermediateRateHz = sourceSampleRateHz / (double)sourceDecimationFactor;
        if (cutoffHz <= 0 || cutoffHz >= Math.Min(intermediateRateHz, outputSampleRateHz) * 0.5)
            throw new ArgumentOutOfRangeException(nameof(cutoffHz));

        int phaseCount = Math.Min(interpolationFactor, maximumExactPhases);
        phaseTaps = new float[phaseCount][];
        reversedPhaseTaps = new float[phaseCount][];
        double center = (tapsPerPhase - 1) * 0.5;
        double normalizedCutoff = cutoffHz / intermediateRateHz;
        for (int phase = 0; phase < phaseCount; phase++)
        {
            double fraction = phase / (double)phaseCount;
            var taps = new float[tapsPerPhase];
            double sum = 0;
            for (int tap = 0; tap < taps.Length; tap++)
            {
                double distance = tap + fraction - center;
                double sinc = distance == 0 ? 2 * normalizedCutoff :
                    Math.Sin(2 * Math.PI * normalizedCutoff * distance) / (Math.PI * distance);
                double window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * tap / (taps.Length - 1)) +
                    0.08 * Math.Cos(4 * Math.PI * tap / (taps.Length - 1));
                taps[tap] = (float)(sinc * window);
                sum += taps[tap];
            }
            for (int tap = 0; tap < taps.Length; tap++) taps[tap] /= (float)sum;
            phaseTaps[phase] = taps;
            float[] reversed = new float[taps.Length];
            for (int tap = 0; tap < taps.Length; tap++) reversed[tap] = taps[taps.Length - 1 - tap];
            reversedPhaseTaps[phase] = reversed;
        }

        ResetState();
    }

    public bool TryProcess(float inputI, float inputQ, out float outputI, out float outputQ)
    {
        EnsureConfigured();
        if (interpolationFactor > decimationFactor)
            throw new InvalidOperationException("Use Process when the resampler is configured for upsampling.");

        Store(inputI, inputQ);
        long sourceIndex = nextOutputNumerator / interpolationFactor;
        if (sourceIndex > inputIndex)
        {
            outputI = outputQ = 0;
            return false;
        }

        Filter(sourceIndex, out outputI, out outputQ);
        nextOutputNumerator += decimationFactor;
        return true;
    }

    public void Process(float inputI, float inputQ, Action<float, float> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        EnsureConfigured();
        Store(inputI, inputQ);
        while (nextOutputNumerator / interpolationFactor <= inputIndex)
        {
            long sourceIndex = nextOutputNumerator / interpolationFactor;
            Filter(sourceIndex, out float outputI, out float outputQ);
            nextOutputNumerator += decimationFactor;
            emit(outputI, outputQ);
        }
    }

    public void Reset()
    {
        EnsureConfigured();
        ResetState();
    }

    private void Store(float inputI, float inputQ)
    {
        if (++historyPosition == tapsPerPhase) historyPosition = 0;
        historyI[historyPosition] = inputI;
        historyQ[historyPosition] = inputQ;
        historyI[historyPosition + tapsPerPhase] = inputI;
        historyQ[historyPosition + tapsPerPhase] = inputQ;
        inputIndex++;
    }

    private void Filter(long sourceIndex, out float outputI, out float outputQ)
    {
        long remainder = nextOutputNumerator % interpolationFactor;
        int phase = (int)((remainder * phaseTaps.Length + interpolationFactor / 2L) /
            interpolationFactor) % phaseTaps.Length;
        float[] taps = phaseTaps[phase];
        float[] reversedTaps = reversedPhaseTaps[phase];
        int delay = Math.Clamp(checked((int)(inputIndex - sourceIndex)), 0, tapsPerPhase - 1);
        int historyIndex = historyPosition - delay;
        while (historyIndex < 0) historyIndex += tapsPerPhase;
        int start = historyIndex - taps.Length + 1;
        if (start < 0) start += tapsPerPhase;
        outputI = Dot(historyI.AsSpan(start, taps.Length), reversedTaps);
        outputQ = Dot(historyQ.AsSpan(start, taps.Length), reversedTaps);
    }

    private void ResetState()
    {
        Array.Clear(historyI);
        Array.Clear(historyQ);
        historyPosition = -1;
        inputIndex = -1;
        nextOutputNumerator = 0;
    }

    private void EnsureConfigured()
    {
        if (interpolationFactor == 0) throw new InvalidOperationException("The resampler is not configured.");
    }

    private static float Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        int index = 0;
        Vector<float> sum = Vector<float>.Zero;
        if (Vector.IsHardwareAccelerated)
        {
            int width = Vector<float>.Count;
            int limit = left.Length - width;
            for (; index <= limit; index += width)
                sum += new Vector<float>(left.Slice(index, width)) *
                    new Vector<float>(right.Slice(index, width));
        }
        float result = Vector.Sum(sum);
        for (; index < left.Length; index++) result += left[index] * right[index];
        return result;
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0) (left, right) = (right, left % right);
        return Math.Abs(left);
    }
}
