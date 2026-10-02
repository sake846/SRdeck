using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Messages;
using SRdeck.Models.SDR;
using SRdeck.Models;

namespace SRdeck.SDR;

public partial class SdrController
{
    private void OnStreamACallback(nint ptrSampleI, nint ptrSampleQ, ref SdrPlayApi.StreamCbParamsT callbackParams, uint numSamples, uint reset, nint callbackContext)
    {
        if (_isStopping) return;
        bool captureTiming = IsTimingCaptureEnabled;
        long callbackStartedTicks = captureTiming ? Stopwatch.GetTimestamp() : 0;
        try
        {
            Interlocked.Increment(ref _callbackCount);
            RecordStreamCallback(numSamples, reset);

            if (reset == 1)
            {
                Debug.Print($"sdrplay_api_StreamCallback: numSamples={numSamples} (Reset)");
            }

            int sampleCount = (int)Math.Min(numSamples, int.MaxValue);
            if (sampleCount <= 0) return;

            lock (_streamCallbackLock)
            {
                Channel<QueuedSampleBlock>? queue = _sampleQueue;
                if (queue == null)
                {
                    Interlocked.Increment(ref _droppedCallbackCount);
                    return;
                }

                short[] samplesI = ArrayPool<short>.Shared.Rent(sampleCount);
                short[] samplesQ = ArrayPool<short>.Shared.Rent(sampleCount);
                SdrSampleMetadata metadata = _sampleClock.Capture(sampleCount, callbackParams.FirstSampleNum, reset != 0);
                metadata = metadata with
                {
                    IsDiscontinuous = metadata.IsDiscontinuous || callbackParams.RfChanged != 0 || callbackParams.FsChanged != 0
                };
                try
                {
                    Marshal.Copy(ptrSampleI, samplesI, 0, sampleCount);
                    Marshal.Copy(ptrSampleQ, samplesQ, 0, sampleCount);

                    long enqueuedTimestampTicks = captureTiming ? Stopwatch.GetTimestamp() : 0;
                    if (!queue.Writer.TryWrite(new QueuedSampleBlock(
                            samplesI,
                            samplesQ,
                            (uint)sampleCount,
                            metadata,
                            enqueuedTimestampTicks)))
                    {
                        Interlocked.Increment(ref _droppedCallbackCount);
                        ReturnSampleBlock(new QueuedSampleBlock(samplesI, samplesQ, numSamples));
                        return;
                    }

                    Interlocked.Increment(ref _enqueuedSampleBlocks);
                }
                catch
                {
                    Interlocked.Increment(ref _droppedCallbackCount);
                    ArrayPool<short>.Shared.Return(samplesI, clearArray: false);
                    ArrayPool<short>.Shared.Return(samplesQ, clearArray: false);
                    Debug.Print("[SdrController] Failed to copy an IQ callback block.");
                }
            }
        }
        finally
        {
            if (captureTiming)
                RecordCallbackElapsed(Stopwatch.GetTimestamp() - callbackStartedTicks);
        }
    }

    private void StartSampleDispatcher()
    {
        lock (_streamCallbackLock)
        {
            Volatile.Write(ref _callbackCount, 0);
            Volatile.Write(ref _droppedCallbackCount, 0);
            Volatile.Write(ref _enqueuedSampleBlocks, 0);
            Volatile.Write(ref _dequeuedSampleBlocks, 0);
            _sampleClock = new SdrSampleClock();
            _sampleQueueCancellation = new CancellationTokenSource();
            _sampleQueue = Channel.CreateBounded<QueuedSampleBlock>(
                new BoundedChannelOptions(SampleQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
            CancellationToken cancellationToken = _sampleQueueCancellation.Token;
            Channel<QueuedSampleBlock> queue = _sampleQueue;
            _sampleDispatchTask = Task.Factory.StartNew(
                () => DispatchSamples(queue, cancellationToken),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    private void DispatchSamples(
        Channel<QueuedSampleBlock> queue,
        CancellationToken cancellationToken)
    {
        var continuity = new SdrSampleDeliveryTracker();
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   queue.Reader.WaitToReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
            {
                while (!cancellationToken.IsCancellationRequested && queue.Reader.TryRead(out QueuedSampleBlock block))
                {
                    Interlocked.Increment(ref _dequeuedSampleBlocks);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        ReturnSampleBlock(block);
                        continue;
                    }

                    bool captureTiming = IsTimingCaptureEnabled;
                    long deliveryStartedTicks = captureTiming ? Stopwatch.GetTimestamp() : 0;
                    if (captureTiming && block.EnqueuedTimestampTicks > 0)
                        RecordQueueWait(deliveryStartedTicks - block.EnqueuedTimestampTicks);

                    try
                    {
                        SdrSampleMetadata metadata = continuity.Observe(block.Metadata, block.SampleCount);
                        SampleBlockReceived?.Invoke(new(block.SamplesI, block.SamplesQ, block.SampleCount, metadata));
                        SamplesReceived?.Invoke(block.SamplesI, block.SamplesQ, block.SampleCount);
                    }
                    catch (Exception exception)
                    {
                        continuity.MarkFailedDelivery();
                        Debug.Print($"[SdrController] IQ consumer failed: {exception}");
                    }
                    finally
                    {
                        if (captureTiming)
                            RecordSampleDeliveryElapsed(Stopwatch.GetTimestamp() - deliveryStartedTicks);
                        ReturnSampleBlock(block);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            while (queue.Reader.TryRead(out QueuedSampleBlock block))
            {
                Interlocked.Increment(ref _dequeuedSampleBlocks);
                ReturnSampleBlock(block);
            }
        }
    }

    private static void ReturnSampleBlock(QueuedSampleBlock block)
    {
        ArrayPool<short>.Shared.Return(block.SamplesI, clearArray: false);
        ArrayPool<short>.Shared.Return(block.SamplesQ, clearArray: false);
    }

    private void StopSampleDispatcher()
    {
        Channel<QueuedSampleBlock>? queue;
        CancellationTokenSource? cancellation;
        Task? dispatchTask;
        lock (_streamCallbackLock)
        {
            queue = _sampleQueue;
            cancellation = _sampleQueueCancellation;
            dispatchTask = _sampleDispatchTask;
            _sampleQueue = null;
            _sampleQueueCancellation = null;
            _sampleDispatchTask = null;
        }

        if (queue == null && dispatchTask == null)
        {
            cancellation?.Dispose();
            return;
        }

        queue?.Writer.TryComplete();
        cancellation?.Cancel();
        if (dispatchTask != null && !dispatchTask.IsCompleted)
        {
            try { dispatchTask.Wait(TimeSpan.FromSeconds(3)); }
            catch (AggregateException exception)
            {
                Debug.Print($"[SdrController] IQ dispatcher stopped with error: {exception.InnerException?.Message ?? exception.Message}");
            }
        }

        cancellation?.Dispose();
    }

    private void OnStreamBCallback(nint ptrSampleI, nint ptrSampleQ, ref SdrPlayApi.StreamCbParamsT callbackParams, uint numSamples, uint reset, nint callbackContext)
    {
        if (reset == 1)
        {
            Debug.Print("sdrplay_api_StreamBCallback: numSamples=" + numSamples);
        }
    }

    private void OnEventCallback(SdrPlayApi.EventT eventId, SdrPlayApi.TunerSelectT tuner, ref SdrPlayApi.EventParamsT callbackParams, nint callbackContext)
    {
        if (_isStopping) return;
        switch (eventId)
        {
            case SdrPlayApi.EventT.GainChange:
                GainHardwareChanged?.Invoke(callbackParams.GainParams.CurrGain, (int)callbackParams.GainParams.GRdB);
                break;
            case SdrPlayApi.EventT.PowerOverloadChange:
                SdrPlayDiagnosticLog.Write(
                    "power-overload",
                    $"tuner={tuner} type={callbackParams.PowerOverloadParams.PoweOverloadChangeType}");
                ExecuteUpdate(
                    "power-overload-ack",
                    tuner,
                    SdrPlayApi.ReasonForUpdateT.Update_Ctrl_OverloadMsgAck,
                    SdrPlayApi.ReasonForUpdateExtension1T.None);
                break;
            case SdrPlayApi.EventT.DeviceRemoved:
                Debug.Print("SDR Device Removed!");
                SdrPlayDiagnosticLog.Write(
                    "device-removed",
                    $"tuner={tuner} callbacks={CallbackCount} dropped={DroppedCallbackCount}");
                _isStreaming = false;
                _isDeviceSelected = false;
                _pdeviceParams = IntPtr.Zero;
                Interlocked.Exchange(ref _deviceRemovalCleanupPending, 1);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    lock (_lifecycleLock)
                    {
                        if (!_isDisposed && Interlocked.Exchange(ref _deviceRemovalCleanupPending, 0) != 0)
                        {
                            ResetApiState(reportErrors: false);
                        }
                    }
                });
                DeviceRemoved?.Invoke();
                break;
        }
    }
}
