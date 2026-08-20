using System;
using System.Diagnostics;
using System.Threading;
using SRdeck.Models;
using SRdeck.Models.SDR;

namespace SRdeck.Services;

public interface IAgcManager : IDisposable
{
    int CurrentGainDb { get; set; }
    AgcReleaseMode ReleaseMode { get; set; }
    void EvaluateManualGain(
        IqSampleExtrema extrema,
        SdrDeviceCapabilities deviceCapabilities,
        int configuredMinimumGain,
        int maximumGain);
}

public interface IAgcManagerFactory
{
    IAgcManager Create(Action applyGainUpdate);
}

public sealed class AgcManagerFactory : IAgcManagerFactory
{
    private readonly IGainUpdateWorkerFactory _gainUpdateWorkerFactory;

    public AgcManagerFactory(IGainUpdateWorkerFactory gainUpdateWorkerFactory)
    {
        _gainUpdateWorkerFactory = gainUpdateWorkerFactory;
    }

    public IAgcManager Create(Action applyGainUpdate) =>
        new AgcManager(_gainUpdateWorkerFactory.Create(applyGainUpdate));
}

internal sealed class AgcManager : IAgcManager
{
    private readonly IGainUpdateWorker _gainUpdateWorker;
    private readonly Func<long> _getTimestamp;
    private int _releaseMode = (int)AgcReleaseMode.Slow;
    private long _nextReleaseTimestamp;

    public AgcManager(IGainUpdateWorker gainUpdateWorker)
        : this(gainUpdateWorker, Stopwatch.GetTimestamp)
    {
    }

    internal AgcManager(IGainUpdateWorker gainUpdateWorker, Func<long> getTimestamp)
    {
        _gainUpdateWorker = gainUpdateWorker;
        _getTimestamp = getTimestamp;
        _nextReleaseTimestamp = getTimestamp() + GetReleaseDelayTicks(AgcReleaseMode.Slow);
    }

    public int CurrentGainDb { get; set; }

    public AgcReleaseMode ReleaseMode
    {
        get => (AgcReleaseMode)Volatile.Read(ref _releaseMode);
        set
        {
            AgcReleaseMode normalized = Enum.IsDefined(value) ? value : AgcReleaseMode.Slow;
            Volatile.Write(ref _releaseMode, (int)normalized);
            long nextRelease = normalized == AgcReleaseMode.AttackOnly
                ? long.MaxValue
                : _getTimestamp() + GetReleaseDelayTicks(normalized);
            Interlocked.Exchange(ref _nextReleaseTimestamp, nextRelease);
        }
    }

    public void EvaluateManualGain(
        IqSampleExtrema extrema,
        SdrDeviceCapabilities deviceCapabilities,
        int configuredMinimumGain,
        int maximumGain)
    {
        ManualAgcDeviceKind deviceKind = deviceCapabilities.IsRtlSdr
            ? ManualAgcDeviceKind.RtlSdr
            : ManualAgcDeviceKind.Generic;
        int minimumGain = deviceCapabilities.IsRtlSdr ? 0 : configuredMinimumGain;
        AgcReleaseMode releaseMode = ReleaseMode;
        var input = new ManualAgcInput(
            extrema.MaxI,
            extrema.MinI,
            extrema.MaxQ,
            extrema.MinQ,
            CurrentGainDb,
            minimumGain,
            maximumGain,
            AppConstants.AGC_UPPER_THRESHOLD,
            AppConstants.AGC_LOWER_THRESHOLD,
            deviceKind);
        long now = _getTimestamp();
        long releaseDelayTicks = GetReleaseDelayTicks(releaseMode);
        if (ManualAgcPolicy.IsOver(input))
        {
            // Attack is immediate. Keep postponing release while overload remains.
            Interlocked.Exchange(
                ref _nextReleaseTimestamp,
                releaseMode == AgcReleaseMode.AttackOnly ? long.MaxValue : now + releaseDelayTicks);
        }

        int nextGain = ManualAgcPolicy.CalculateNextGain(input);
        if (nextGain == CurrentGainDb)
        {
            return;
        }

        bool isRelease = deviceKind == ManualAgcDeviceKind.RtlSdr
            ? nextGain > CurrentGainDb
            : nextGain < CurrentGainDb;
        if (isRelease)
        {
            if (releaseMode == AgcReleaseMode.AttackOnly ||
                now < Interlocked.Read(ref _nextReleaseTimestamp)) return;
            Interlocked.Exchange(ref _nextReleaseTimestamp, now + releaseDelayTicks);
        }

        CurrentGainDb = nextGain;
        _gainUpdateWorker.RequestUpdate();
    }

    private static long GetReleaseDelayTicks(AgcReleaseMode mode)
    {
        double seconds = mode switch
        {
            AgcReleaseMode.Fast => AppConstants.AGC_RELEASE_FAST_SECONDS,
            AgcReleaseMode.Medium => AppConstants.AGC_RELEASE_MEDIUM_SECONDS,
            AgcReleaseMode.Slow => AppConstants.AGC_RELEASE_SLOW_SECONDS,
            _ => AppConstants.AGC_RELEASE_SLOW_SECONDS
        };
        return (long)(seconds * Stopwatch.Frequency);
    }


    public void Dispose() => _gainUpdateWorker.Dispose();
}
