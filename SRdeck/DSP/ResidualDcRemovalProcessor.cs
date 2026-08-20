using System;

namespace SRdeck.DSP;

/// <summary>
/// Removes the slowly varying complex DC component from an IQ stream.
/// The processor keeps state across input blocks so a block boundary does not
/// introduce a discontinuity into the stored IQ signal.
/// </summary>
public sealed class ResidualDcRemovalProcessor
{
    // A 5 Hz corner only suppresses a slowly moving LO leak by a few dB once
    // its energy spreads above the first couple of hertz.  Use a modestly
    // wider corner so the residual centre spike is visibly reduced while
    // preserving the useful IQ passband for narrow-band demodulators.
    private const double CutoffFrequencyHz = 20.0;

    private double _dcI;
    private double _dcQ;
    private bool _hasEstimate;

    public void Process(
        ReadOnlySpan<short> sourceI,
        ReadOnlySpan<short> sourceQ,
        Span<short> destinationI,
        Span<short> destinationQ,
        int sampleRateHz)
    {
        int sampleCount = Math.Min(
            Math.Min(sourceI.Length, sourceQ.Length),
            Math.Min(destinationI.Length, destinationQ.Length));
        if (sampleCount == 0) return;

        if (sampleRateHz <= 0)
        {
            sourceI[..sampleCount].CopyTo(destinationI);
            sourceQ[..sampleCount].CopyTo(destinationQ);
            return;
        }

        if (!_hasEstimate)
        {
            _dcI = CalculateMean(sourceI[..sampleCount]);
            _dcQ = CalculateMean(sourceQ[..sampleCount]);
            _hasEstimate = true;
        }

        double alpha = 1.0 - Math.Exp(-2.0 * Math.PI * CutoffFrequencyHz / sampleRateHz);
        for (int index = 0; index < sampleCount; index++)
        {
            double inputI = sourceI[index];
            double inputQ = sourceQ[index];
            _dcI += alpha * (inputI - _dcI);
            _dcQ += alpha * (inputQ - _dcQ);

            destinationI[index] = SaturateToInt16(inputI - _dcI);
            destinationQ[index] = SaturateToInt16(inputQ - _dcQ);
        }
    }

    public void Reset()
    {
        _dcI = 0.0;
        _dcQ = 0.0;
        _hasEstimate = false;
    }

    private static double CalculateMean(ReadOnlySpan<short> samples)
    {
        double sum = 0.0;
        for (int index = 0; index < samples.Length; index++) sum += samples[index];
        return sum / samples.Length;
    }

    private static short SaturateToInt16(double value)
    {
        if (value >= short.MaxValue) return short.MaxValue;
        if (value <= short.MinValue) return short.MinValue;
        return (short)Math.Round(value, MidpointRounding.AwayFromZero);
    }
}
