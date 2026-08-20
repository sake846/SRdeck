using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SRdeck.Models.SDR;

namespace SRdeck.SDR;

public partial class SdrController
{
    private const int StreamStallTimeoutSeconds = 5;
    private const int StreamHeartbeatSeconds = 10;

    private readonly object _apiUpdateLock = new();
    private CancellationTokenSource? _streamWatchdogCancellation;
    private Task? _streamWatchdogTask;
    private long _lastCallbackTimestamp;
    private long _lastHeartbeatTimestamp;
    private int _streamStallReported;
    private int _firstCallbackLogged;
    private int _updateErrorReported;

    public double LastCallbackAgeSeconds
    {
        get
        {
            long timestamp = Interlocked.Read(ref _lastCallbackTimestamp);
            return timestamp <= 0
                ? double.PositiveInfinity
                : Stopwatch.GetElapsedTime(timestamp).TotalSeconds;
        }
    }

    private void StartStreamWatchdog()
    {
        StopStreamWatchdog();

        long now = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _lastCallbackTimestamp, now);
        Interlocked.Exchange(ref _lastHeartbeatTimestamp, now);
        Interlocked.Exchange(ref _streamStallReported, 0);
        Interlocked.Exchange(ref _firstCallbackLogged, 0);
        Interlocked.Exchange(ref _updateErrorReported, 0);

        var cancellation = new CancellationTokenSource();
        _streamWatchdogCancellation = cancellation;
        _streamWatchdogTask = Task.Run(() => WatchStreamAsync(cancellation.Token));
        SdrPlayDiagnosticLog.Write(
            "watchdog-start",
            $"timeoutSeconds={StreamStallTimeoutSeconds} log={SdrPlayDiagnosticLog.LogPath}");
    }

    private async Task WatchStreamAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                long now = Stopwatch.GetTimestamp();
                long lastCallback = Interlocked.Read(ref _lastCallbackTimestamp);
                long lastHeartbeat = Interlocked.Read(ref _lastHeartbeatTimestamp);
                bool alreadyReported = Volatile.Read(ref _streamStallReported) != 0;

                if (now - lastHeartbeat >= StreamHeartbeatSeconds * Stopwatch.Frequency &&
                    Interlocked.CompareExchange(ref _lastHeartbeatTimestamp, now, lastHeartbeat) == lastHeartbeat)
                {
                    SdrPlayDiagnosticLog.Write(
                        "stream-heartbeat",
                        $"callbacks={CallbackCount} dropped={DroppedCallbackCount} queued={QueuedSampleBlockCount} lastCallbackAgeMs={LastCallbackAgeSeconds * 1000.0:F0}");
                }

                if (!SdrStreamLivenessPolicy.ShouldReportStall(
                        Volatile.Read(ref _isStreaming),
                        Volatile.Read(ref _isStopping),
                        alreadyReported,
                        lastCallback,
                        now,
                        StreamStallTimeoutSeconds * Stopwatch.Frequency))
                {
                    continue;
                }

                if (Interlocked.Exchange(ref _streamStallReported, 1) != 0)
                {
                    continue;
                }

                SdrPlayDiagnosticLog.Write(
                    "stream-stalled",
                    $"callbacks={CallbackCount} dropped={DroppedCallbackCount} queued={QueuedSampleBlockCount} lastCallbackAgeMs={LastCallbackAgeSeconds * 1000.0:F0}");
                StreamStalled?.Invoke();
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SdrPlayDiagnosticLog.Write("watchdog-error", exception.ToString());
        }
    }

    private void StopStreamWatchdog()
    {
        CancellationTokenSource? cancellation = _streamWatchdogCancellation;
        Task? task = _streamWatchdogTask;
        _streamWatchdogCancellation = null;
        _streamWatchdogTask = null;

        if (cancellation == null)
        {
            return;
        }

        cancellation.Cancel();
        if (task != null && !task.IsCompleted && Task.CurrentId != task.Id)
        {
            try
            {
                task.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException exception) when (
                exception.InnerExceptions.Count == 1 &&
                exception.InnerException is OperationCanceledException)
            {
            }
        }

        cancellation.Dispose();
        SdrPlayDiagnosticLog.Write("watchdog-stop", $"callbacks={CallbackCount} dropped={DroppedCallbackCount}");
    }

    private void RecordStreamCallback(uint numSamples, uint reset)
    {
        long now = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _lastCallbackTimestamp, now);

        if (Interlocked.Exchange(ref _firstCallbackLogged, 1) == 0)
        {
            SdrPlayDiagnosticLog.Write("stream-first-callback", $"samples={numSamples} reset={reset}");
        }
        else if (reset == 1)
        {
            SdrPlayDiagnosticLog.Write("stream-reset", $"samples={numSamples} callbacks={CallbackCount}");
        }
    }

    private SdrPlayApi.ErrT ExecuteUpdate(
        string operation,
        SdrPlayApi.TunerSelectT tuner,
        SdrPlayApi.ReasonForUpdateT reason,
        SdrPlayApi.ReasonForUpdateExtension1T reasonExtension,
        Action? synchronizeParameters = null)
    {
        lock (_apiUpdateLock)
        {
            if (_pdeviceParams == IntPtr.Zero ||
                !Volatile.Read(ref _isStreaming) ||
                Volatile.Read(ref _isStopping))
            {
                SdrPlayDiagnosticLog.Write(
                    "update-skipped",
                    $"operation={operation} streaming={_isStreaming} stopping={_isStopping} params=0x{_pdeviceParams:X}");
                return SdrPlayApi.ErrT.NotInitialised;
            }

            synchronizeParameters?.Invoke();
            long started = Stopwatch.GetTimestamp();
            SdrPlayDiagnosticLog.Write(
                "update-start",
                $"operation={operation} tuner={tuner} reason={reason} extension={reasonExtension} gr={RfGainDb} lna={LnaState} frequency={CenterFreqHz}");

            SdrPlayApi.ErrT result = SdrPlayApi.sdrplay_api_Update(
                _devices[0].Dev,
                tuner,
                reason,
                reasonExtension);

            SdrPlayDiagnosticLog.Write(
                "update-end",
                $"operation={operation} result={result} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} callbacks={CallbackCount}");
            if (result != SdrPlayApi.ErrT.Success)
            {
                bool transient = IsTransientUpdateResult(result);
                bool notifyUser = !transient &&
                    Interlocked.Exchange(ref _updateErrorReported, 1) == 0;
                HandleSdrError(
                    $"SdrPlayApi.sdrplay_api_Update {operation} failed",
                    result,
                    notifyUser);
            }

            return result;
        }
    }

    internal static bool IsTransientUpdateResult(SdrPlayApi.ErrT result) => result is
        SdrPlayApi.ErrT.NotInitialised or
        SdrPlayApi.ErrT.StartPending or
        SdrPlayApi.ErrT.StopPending;
}
