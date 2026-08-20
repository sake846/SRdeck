using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Messages;
using SRdeck.Models.SDR;

namespace SRdeck.SDR;

public partial class SdrController
{
    private void OnStreamACallback(nint ptrSampleI, nint ptrSampleQ, ref SdrPlayApi.StreamCbParamsT callbackParams, uint numSamples, uint reset, nint callbackContext)
    {
        if (_isStopping) return;
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
            try
            {
                Marshal.Copy(ptrSampleI, samplesI, 0, sampleCount);
                Marshal.Copy(ptrSampleQ, samplesQ, 0, sampleCount);

                if (!queue.Writer.TryWrite(new QueuedSampleBlock(samplesI, samplesQ, numSamples)))
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

    private void StartSampleDispatcher()
    {
        lock (_streamCallbackLock)
        {
            Volatile.Write(ref _callbackCount, 0);
            Volatile.Write(ref _droppedCallbackCount, 0);
            Volatile.Write(ref _enqueuedSampleBlocks, 0);
            Volatile.Write(ref _dequeuedSampleBlocks, 0);
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
        try
        {
            while (queue.Reader.WaitToReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
            {
                while (queue.Reader.TryRead(out QueuedSampleBlock block))
                {
                    Interlocked.Increment(ref _dequeuedSampleBlocks);
                    try
                    {
                        SamplesReceived?.Invoke(block.SamplesI, block.SamplesQ, block.SampleCount);
                    }
                    catch (Exception exception)
                    {
                        Debug.Print($"[SdrController] IQ consumer failed: {exception}");
                    }
                    finally
                    {
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
