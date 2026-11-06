using System.Diagnostics;
using SRdeck.Models;
using SRdeck.Models.SDR;

namespace SRdeck.Services;

public interface IAgcManager : IDisposable
{
    int CurrentGainDb { get; set; }
    AgcReleaseMode ReleaseMode { get; set; }
    AgcControlSettings ControlSettings { get; set; }
    void EvaluateManualGain(
        IqSampleExtrema extrema,
        SdrDeviceCapabilities deviceCapabilities,
        int configuredMinimumGain,
        int maximumGain);
}

internal sealed class AgcManager : IAgcManager
{
    private readonly IGainUpdateWorker _gainUpdateWorker;
    private readonly Func<long> _getTimestamp;
    private int _releaseMode = (int)AgcReleaseMode.Slow;
    private AgcControlSettings _controlSettings = AgcControlSettings.Default;
    private long _nextReleaseTimestamp;
    private long _nextAttackTimestamp = long.MinValue;

    public AgcManager(IGainUpdateWorker gainUpdateWorker)
        : this(gainUpdateWorker, Stopwatch.GetTimestamp)
    {
    }

    internal AgcManager(IGainUpdateWorker gainUpdateWorker, Func<long> getTimestamp)
    {
        _gainUpdateWorker = gainUpdateWorker;
        _getTimestamp = getTimestamp;
        _nextReleaseTimestamp = getTimestamp() + GetSecondsTicks(ControlSettings.HoldSeconds);
    }

    public int CurrentGainDb { get; set; }

    public AgcControlSettings ControlSettings
    {
        get => Volatile.Read(ref _controlSettings);
        set
        {
            Volatile.Write(ref _controlSettings, value ?? AgcControlSettings.Default);
            Interlocked.Exchange(ref _nextAttackTimestamp, long.MinValue);
            Interlocked.Exchange(
                ref _nextReleaseTimestamp,
                _getTimestamp() + GetSecondsTicks(ControlSettings.HoldSeconds));
        }
    }

    public AgcReleaseMode ReleaseMode
    {
        get => (AgcReleaseMode)Volatile.Read(ref _releaseMode);
        set
        {
            AgcReleaseMode normalized = Enum.IsDefined(value) ? value : AgcReleaseMode.Slow;
            Volatile.Write(ref _releaseMode, (int)normalized);
            long nextRelease = normalized == AgcReleaseMode.AttackOnly
                ? long.MaxValue
                : _getTimestamp() + GetSecondsTicks(ControlSettings.HoldSeconds);
            Interlocked.Exchange(ref _nextReleaseTimestamp, nextRelease);
            Interlocked.Exchange(ref _nextAttackTimestamp, long.MinValue);
        }
    }

    public void EvaluateManualGain(
        IqSampleExtrema extrema,
        SdrDeviceCapabilities deviceCapabilities,
        int configuredMinimumGain,
        int maximumGain)
    {
        ManualAgcDeviceKind deviceKind = deviceCapabilities.Kind switch
        {
            SdrDeviceKind.RtlSdr => ManualAgcDeviceKind.RtlSdr,
            SdrDeviceKind.HackRf => ManualAgcDeviceKind.HackRf,
            SdrDeviceKind.Rx888 => ManualAgcDeviceKind.Rx888,
            _ => ManualAgcDeviceKind.Generic
        };
        int minimumGain = deviceCapabilities.IsRtlSdr || deviceCapabilities.IsHackRf || deviceCapabilities.IsRx888
            ? 0 : configuredMinimumGain;
        AgcReleaseMode releaseMode = ReleaseMode;
        AgcControlSettings settings = ControlSettings;
        var input = new ManualAgcInput(
            extrema.MaxI,
            extrema.MinI,
            extrema.MaxQ,
            extrema.MinQ,
            CurrentGainDb,
            minimumGain,
            maximumGain,
            settings.UpperThreshold,
            settings.LowerThreshold,
            deviceKind);
        long now = _getTimestamp();
        long attackDelayTicks = GetSecondsTicks(settings.AttackSeconds);
        long releaseDelayTicks = GetSecondsTicks(settings.ReleaseSeconds);
        bool isOver = ManualAgcPolicy.IsOver(input);
        if (isOver)
        {
            // Keep the release hold running from the last overloaded block.
            Interlocked.Exchange(
                ref _nextReleaseTimestamp,
                releaseMode == AgcReleaseMode.AttackOnly ? long.MaxValue : now + GetSecondsTicks(settings.HoldSeconds));
        }
        else
        {
            Interlocked.Exchange(ref _nextAttackTimestamp, long.MinValue);
        }

        int nextGain = ManualAgcPolicy.CalculateNextGain(input);
        if (nextGain == CurrentGainDb)
        {
            return;
        }

        bool isRelease = deviceKind is ManualAgcDeviceKind.RtlSdr or ManualAgcDeviceKind.HackRf or ManualAgcDeviceKind.Rx888
            ? nextGain > CurrentGainDb
            : nextGain < CurrentGainDb;
        if (isRelease)
        {
            if (releaseMode == AgcReleaseMode.AttackOnly ||
                now < Interlocked.Read(ref _nextReleaseTimestamp)) return;
            Interlocked.Exchange(ref _nextReleaseTimestamp, now + releaseDelayTicks);
        }
        else if (isOver)
        {
            long nextAttack = Interlocked.Read(ref _nextAttackTimestamp);
            if (nextAttack != long.MinValue && now < nextAttack) return;
            Interlocked.Exchange(ref _nextAttackTimestamp, now + attackDelayTicks);
        }

        CurrentGainDb = nextGain;
        _gainUpdateWorker.RequestUpdate();
    }

    private static long GetSecondsTicks(double seconds) =>
        (long)(Math.Clamp(double.IsFinite(seconds) ? seconds : 0, 0, 3600) * Stopwatch.Frequency);


    public void Dispose() => _gainUpdateWorker.Dispose();
}
