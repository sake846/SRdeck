using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SRdeck.SDR;

/// <summary>
/// Keeps managed IQ conversion and downstream processing out of librtlsdr's
/// libusb event callback. librtlsdr does not resubmit a completed transfer
/// until that callback returns, so doing the full signal pipeline there can
/// eventually exhaust all outstanding USB transfers during a scheduler or GC
/// pause.
/// </summary>
internal sealed class RtlSdrSampleDispatcher : IDisposable
{
    // 64 default RTL blocks hold about 4.2 seconds at 2 MS/s. This is large
    // enough to absorb an exceptional Windows scheduling pause without making
    // an unbounded queue capable of exhausting process memory.
    internal const int QueueCapacity = 64;

    private readonly Action<short[], short[], uint> _samplesReceived;
    private readonly ArrayPool<byte> _bytePool;
    private readonly ArrayPool<short> _shortPool;
    private readonly int _expectedBlockLength;
    private readonly object _gate = new();
    private Channel<RawSampleBlock>? _queue;
    private CancellationTokenSource? _cancellation;
    private Task? _dispatchTask;
    private long _callbackCount;
    private long _droppedCallbackCount;
    private long _enqueuedBlocks;
    private long _dequeuedBlocks;
    private long _lastCallbackTimestamp;
    private int _lastCallbackLength;
    private long _unexpectedCallbackLengthCount;
    private int _disposed;

    private readonly record struct RawSampleBlock(byte[] Bytes, int Length);

    public RtlSdrSampleDispatcher(
        Action<short[], short[], uint> samplesReceived,
        int expectedBlockLength = 0,
        ArrayPool<byte>? bytePool = null,
        ArrayPool<short>? shortPool = null)
    {
        _samplesReceived = samplesReceived ?? throw new ArgumentNullException(nameof(samplesReceived));
        _expectedBlockLength = Math.Max(0, expectedBlockLength);
        _bytePool = bytePool ?? ArrayPool<byte>.Shared;
        _shortPool = shortPool ?? ArrayPool<short>.Shared;
    }

    public int QueuedBlockCount => Math.Max(
        0,
        (int)Math.Min(
            int.MaxValue,
            Interlocked.Read(ref _enqueuedBlocks) -
            Interlocked.Read(ref _dequeuedBlocks)));

    public long CallbackCount => Interlocked.Read(ref _callbackCount);

    public long DroppedCallbackCount => Interlocked.Read(ref _droppedCallbackCount);

    public int LastCallbackLength => Volatile.Read(ref _lastCallbackLength);

    public long UnexpectedCallbackLengthCount =>
        Interlocked.Read(ref _unexpectedCallbackLengthCount);

    public double LastCallbackAgeSeconds
    {
        get
        {
            long timestamp = Interlocked.Read(ref _lastCallbackTimestamp);
            return timestamp == 0
                ? double.PositiveInfinity
                : Stopwatch.GetElapsedTime(timestamp).TotalSeconds;
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_gate)
        {
            if (_queue is not null) return;
            Volatile.Write(ref _callbackCount, 0);
            Volatile.Write(ref _droppedCallbackCount, 0);
            Volatile.Write(ref _enqueuedBlocks, 0);
            Volatile.Write(ref _dequeuedBlocks, 0);
            Volatile.Write(ref _lastCallbackTimestamp, 0);
            Volatile.Write(ref _lastCallbackLength, 0);
            Volatile.Write(ref _unexpectedCallbackLengthCount, 0);
            _cancellation = new CancellationTokenSource();
            _queue = Channel.CreateBounded<RawSampleBlock>(
                new BoundedChannelOptions(QueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false
                });
            Channel<RawSampleBlock> queue = _queue;
            CancellationToken cancellationToken = _cancellation.Token;
            _dispatchTask = Task.Factory.StartNew(
                () => Dispatch(queue, cancellationToken),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    public bool TryEnqueue(IntPtr source, int length)
    {
        RecordCallback(length);
        if (source == IntPtr.Zero || length < 2 || (length & 1) != 0)
        {
            Interlocked.Increment(ref _droppedCallbackCount);
            return false;
        }

        byte[] raw = _bytePool.Rent(length);
        try
        {
            Marshal.Copy(source, raw, 0, length);
            return TryEnqueueOwned(raw, length);
        }
        catch (Exception exception)
        {
            _bytePool.Return(raw, clearArray: false);
            Interlocked.Increment(ref _droppedCallbackCount);
            Debug.WriteLine($"[RtlSdrController] Failed to copy an IQ callback block: {exception.Message}");
            return false;
        }
    }

    internal bool TryEnqueue(ReadOnlySpan<byte> source)
    {
        RecordCallback(source.Length);
        if (source.Length < 2 || (source.Length & 1) != 0)
        {
            Interlocked.Increment(ref _droppedCallbackCount);
            return false;
        }

        byte[] raw = _bytePool.Rent(source.Length);
        source.CopyTo(raw);
        return TryEnqueueOwned(raw, source.Length);
    }

    private bool TryEnqueueOwned(byte[] raw, int length)
    {
        lock (_gate)
        {
            Channel<RawSampleBlock>? queue = _queue;
            if (queue is not null && queue.Writer.TryWrite(new RawSampleBlock(raw, length)))
            {
                Interlocked.Increment(ref _enqueuedBlocks);
                return true;
            }
        }

        _bytePool.Return(raw, clearArray: false);
        Interlocked.Increment(ref _droppedCallbackCount);
        return false;
    }

    private void RecordCallback(int length)
    {
        Interlocked.Increment(ref _callbackCount);
        Interlocked.Exchange(ref _lastCallbackTimestamp, Stopwatch.GetTimestamp());
        Volatile.Write(ref _lastCallbackLength, length);
        if (_expectedBlockLength > 0 && length != _expectedBlockLength)
            Interlocked.Increment(ref _unexpectedCallbackLengthCount);
    }

    private void Dispatch(Channel<RawSampleBlock> queue, CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                Thread.CurrentThread.Name ??= "RTL-SDR IQ dispatcher";
                Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[RtlSdrController] Failed to configure IQ dispatcher: {exception.Message}");
            }

            while (queue.Reader.WaitToReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
            {
                while (queue.Reader.TryRead(out RawSampleBlock block))
                {
                    Interlocked.Increment(ref _dequeuedBlocks);
                    DispatchBlock(block);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            while (queue.Reader.TryRead(out RawSampleBlock block))
            {
                Interlocked.Increment(ref _dequeuedBlocks);
                _bytePool.Return(block.Bytes, clearArray: false);
            }
            try { Thread.CurrentThread.Priority = ThreadPriority.Normal; }
            catch { /* Best effort during worker shutdown. */ }
        }
    }

    private void DispatchBlock(RawSampleBlock block)
    {
        int sampleCount = block.Length / 2;
        short[] samplesI = _shortPool.Rent(sampleCount);
        short[] samplesQ = _shortPool.Rent(sampleCount);
        try
        {
            try
            {
                ConvertUnsignedIq(
                    block.Bytes.AsSpan(0, block.Length),
                    samplesI.AsSpan(0, sampleCount),
                    samplesQ.AsSpan(0, sampleCount));
            }
            finally
            {
                _bytePool.Return(block.Bytes, clearArray: false);
            }

            try
            {
                _samplesReceived(samplesI, samplesQ, (uint)sampleCount);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[RtlSdrController] IQ consumer failed: {exception}");
            }
        }
        finally
        {
            _shortPool.Return(samplesI, clearArray: false);
            _shortPool.Return(samplesQ, clearArray: false);
        }
    }

    internal static void ConvertUnsignedIq(
        ReadOnlySpan<byte> raw,
        Span<short> samplesI,
        Span<short> samplesQ)
    {
        int sampleCount = Math.Min(raw.Length / 2, Math.Min(samplesI.Length, samplesQ.Length));
        for (int index = 0; index < sampleCount; index++)
        {
            samplesI[index] = RtlSdrController.ConvertUnsignedSample(raw[index * 2]);
            samplesQ[index] = RtlSdrController.ConvertUnsignedSample(raw[index * 2 + 1]);
        }
    }

    public void Stop()
    {
        Channel<RawSampleBlock>? queue;
        CancellationTokenSource? cancellation;
        Task? dispatchTask;
        lock (_gate)
        {
            queue = _queue;
            cancellation = _cancellation;
            dispatchTask = _dispatchTask;
            _queue = null;
            _cancellation = null;
            _dispatchTask = null;
        }

        queue?.Writer.TryComplete();
        cancellation?.Cancel();
        if (dispatchTask is not null && !dispatchTask.IsCompleted)
        {
            try
            {
                if (!dispatchTask.Wait(TimeSpan.FromSeconds(3)))
                    Debug.WriteLine("[RtlSdrController] IQ dispatcher stop timed out.");
            }
            catch (AggregateException exception)
            {
                Debug.WriteLine($"[RtlSdrController] IQ dispatcher stopped with error: {exception.InnerException?.Message ?? exception.Message}");
            }
        }
        cancellation?.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
    }
}
