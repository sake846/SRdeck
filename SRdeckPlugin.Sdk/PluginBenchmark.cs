using System.Diagnostics;

namespace SRdeckPlugin.Sdk;

public sealed record PluginBenchmarkResult(
    int Iterations,
    TimeSpan Elapsed,
    long AllocatedBytes,
    double InputDurationSeconds,
    double RealtimeFactor)
{
    public double AverageMilliseconds => Elapsed.TotalMilliseconds / Iterations;
    public double AllocatedBytesPerIteration => AllocatedBytes / (double)Iterations;
}

public static class PluginBenchmark
{
    public static PluginBenchmarkResult Run(
        Action operation,
        int iterations,
        double inputDurationSecondsPerIteration,
        int warmupIterations = 1)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));
        if (warmupIterations < 0) throw new ArgumentOutOfRangeException(nameof(warmupIterations));
        if (!double.IsFinite(inputDurationSecondsPerIteration) || inputDurationSecondsPerIteration < 0)
            throw new ArgumentOutOfRangeException(nameof(inputDurationSecondsPerIteration));

        for (int index = 0; index < warmupIterations; index++) operation();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int index = 0; index < iterations; index++) operation();
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        double inputDuration = inputDurationSecondsPerIteration * iterations;
        double realtimeFactor = elapsed.TotalSeconds == 0
            ? double.PositiveInfinity
            : inputDuration / elapsed.TotalSeconds;
        return new(iterations, elapsed, allocated, inputDuration, realtimeFactor);
    }
}
