namespace SRdeckCore.SignalProcessing;

/// <summary>
/// Streaming normalized CIC-equivalent decimator implemented as cascaded bounded
/// moving sums, avoiding unbounded integrator growth during long-running reception.
/// </summary>
public sealed class BoundedCicDecimator
{
    private double[] delayI = [];
    private double[] delayQ = [];
    private double[] sumI = [];
    private double[] sumQ = [];
    private int factor;
    private int stages;
    private int position;
    private int count;
    private double inverseGain;

    public int DecimationFactor => factor;
    public int StageCount => stages;
    public int GroupDelayInputSamples => stages * (factor - 1) / 2;

    public void Configure(int decimationFactor, int stageCount)
    {
        if (stageCount <= 0) throw new ArgumentOutOfRangeException(nameof(stageCount));
        factor = Math.Max(1, decimationFactor);
        stages = stageCount;
        delayI = new double[stages * factor];
        delayQ = new double[stages * factor];
        sumI = new double[stages];
        sumQ = new double[stages];
        position = 0;
        count = 0;
        inverseGain = 1d / Math.Pow(factor, stages);
    }

    public bool TryProcess(float inputI, float inputQ, out float outputI, out float outputQ)
    {
        if (stages == 0) throw new InvalidOperationException("The decimator is not configured.");

        double i = inputI;
        double q = inputQ;
        int delayIndex = position;
        for (int stage = 0; stage < stages; stage++, delayIndex += factor)
        {
            double previousI = delayI[delayIndex];
            double previousQ = delayQ[delayIndex];
            delayI[delayIndex] = i;
            delayQ[delayIndex] = q;
            sumI[stage] += i - previousI;
            sumQ[stage] += q - previousQ;
            i = sumI[stage];
            q = sumQ[stage];
        }

        if (++position == factor) position = 0;
        if (++count < factor)
        {
            outputI = outputQ = 0;
            return false;
        }

        count = 0;
        outputI = (float)(i * inverseGain);
        outputQ = (float)(q * inverseGain);
        return true;
    }

    public void Reset()
    {
        Array.Clear(delayI);
        Array.Clear(delayQ);
        Array.Clear(sumI);
        Array.Clear(sumQ);
        position = 0;
        count = 0;
    }
}
