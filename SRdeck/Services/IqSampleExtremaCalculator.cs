using System;
using System.Numerics;

namespace SRdeck.Services;

public readonly record struct IqSampleExtrema(short MaxI, short MinI, short MaxQ, short MinQ);

public interface IIqSampleExtremaCalculator
{
    IqSampleExtrema Extend(
        IqSampleExtrema current,
        ReadOnlySpan<short> samplesI,
        ReadOnlySpan<short> samplesQ);
}

public sealed class IqSampleExtremaCalculator : IIqSampleExtremaCalculator
{
    public IqSampleExtrema Extend(
        IqSampleExtrema current,
        ReadOnlySpan<short> samplesI,
        ReadOnlySpan<short> samplesQ)
    {
        short maxI = current.MaxI;
        short minI = current.MinI;
        short maxQ = current.MaxQ;
        short minQ = current.MinQ;

        int index = 0;
        if (Vector.IsHardwareAccelerated && samplesI.Length >= Vector<short>.Count)
        {
            Vector<short> maxVectorI = new(maxI);
            Vector<short> minVectorI = new(minI);
            Vector<short> maxVectorQ = new(maxQ);
            Vector<short> minVectorQ = new(minQ);
            int simdEnd = samplesI.Length - samplesI.Length % Vector<short>.Count;

            for (; index < simdEnd; index += Vector<short>.Count)
            {
                Vector<short> vectorI = new(samplesI.Slice(index, Vector<short>.Count));
                Vector<short> vectorQ = new(samplesQ.Slice(index, Vector<short>.Count));
                maxVectorI = Vector.Max(maxVectorI, vectorI);
                minVectorI = Vector.Min(minVectorI, vectorI);
                maxVectorQ = Vector.Max(maxVectorQ, vectorQ);
                minVectorQ = Vector.Min(minVectorQ, vectorQ);
            }

            for (int laneIndex = 0; laneIndex < Vector<short>.Count; laneIndex++)
            {
                maxI = Math.Max(maxI, maxVectorI[laneIndex]);
                minI = Math.Min(minI, minVectorI[laneIndex]);
                maxQ = Math.Max(maxQ, maxVectorQ[laneIndex]);
                minQ = Math.Min(minQ, minVectorQ[laneIndex]);
            }
        }

        for (; index < samplesI.Length; index++)
        {
            maxI = Math.Max(maxI, samplesI[index]);
            minI = Math.Min(minI, samplesI[index]);
            maxQ = Math.Max(maxQ, samplesQ[index]);
            minQ = Math.Min(minQ, samplesQ[index]);
        }

        return new IqSampleExtrema(maxI, minI, maxQ, minQ);
    }
}
