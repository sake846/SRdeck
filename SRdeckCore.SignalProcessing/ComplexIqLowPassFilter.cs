namespace SRdeckCore.SignalProcessing;

/// <summary>
/// Streaming fourth-order Butterworth low-pass applied independently to I and
/// Q. This is intended for the demodulator's post-channelization RF selectivity
/// and is deliberately separate from a rate-conversion anti-aliasing filter.
/// </summary>
public sealed class ComplexIqLowPassFilter
{
    private readonly Biquad firstI = new(0.5411961f);
    private readonly Biquad firstQ = new(0.5411961f);
    private readonly Biquad secondI = new(1.306563f);
    private readonly Biquad secondQ = new(1.306563f);
    private int sampleRateHz;

    public void Configure(int sampleRateHz, double cutoffHz)
    {
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (cutoffHz <= 0 || cutoffHz >= sampleRateHz * 0.5)
            throw new ArgumentOutOfRangeException(nameof(cutoffHz));

        this.sampleRateHz = sampleRateHz;
        firstI.Configure(sampleRateHz, cutoffHz);
        firstQ.Configure(sampleRateHz, cutoffHz);
        secondI.Configure(sampleRateHz, cutoffHz);
        secondQ.Configure(sampleRateHz, cutoffHz);
    }

    public void Process(float inputI, float inputQ, out float outputI, out float outputQ)
    {
        if (sampleRateHz == 0)
            throw new InvalidOperationException("The IQ low-pass filter is not configured.");
        outputI = secondI.Process(firstI.Process(inputI));
        outputQ = secondQ.Process(firstQ.Process(inputQ));
    }

    public void Reset()
    {
        firstI.Reset();
        firstQ.Reset();
        secondI.Reset();
        secondQ.Reset();
    }

    private sealed class Biquad(float q)
    {
        private float b0;
        private float b1;
        private float b2;
        private float a1;
        private float a2;
        private float z1;
        private float z2;

        public void Configure(int sampleRateHz, double cutoffHz)
        {
            double omega = 2 * Math.PI * cutoffHz / sampleRateHz;
            double cosine = Math.Cos(omega);
            double alpha = Math.Sin(omega) / (2 * q);
            double inverseA0 = 1 / (1 + alpha);
            b0 = (float)((1 - cosine) * 0.5 * inverseA0);
            b1 = (float)((1 - cosine) * inverseA0);
            b2 = b0;
            a1 = (float)(-2 * cosine * inverseA0);
            a2 = (float)((1 - alpha) * inverseA0);
            Reset();
        }

        public float Process(float input)
        {
            float output = b0 * input + z1;
            z1 = b1 * input - a1 * output + z2;
            z2 = b2 * input - a2 * output;
            return output;
        }

        public void Reset() => z1 = z2 = 0;
    }
}
