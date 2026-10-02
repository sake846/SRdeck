using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using SRdeck.Models;

namespace SRdeck.SDR;

/// <summary>
/// Copies native float IQ callbacks into a bounded queue so USB/native workers
/// never run the managed signal pipeline inline.
/// </summary>
internal sealed class Rx888SampleDispatcher : IDisposable
{
    internal const int QueueCapacity = 32;

    private readonly Action<short[], short[], uint> _samplesReceived;
    private readonly Action<SdrSampleBlock>? _sampleBlockReceived;
    private readonly ArrayPool<float> _floatPool;
    private readonly ArrayPool<short> _shortPool;
    private readonly int _expectedBlockLengthBytes;
    private readonly object _gate = new();
    private Channel<RawSampleBlock>? _queue;
    private CancellationTokenSource? _cancellation;
    private Task? _dispatchTask;
    private long _callbackCount;
    private long _sourceSampleCount;
    private long _droppedCallbackCount;
    private long _enqueuedBlocks;
    private long _dequeuedBlocks;
    private long _lastCallbackTimestamp;
    private int _lastCallbackLengthBytes;
    private long _unexpectedCallbackLengthCount;
    private int _disposed;

    private readonly record struct RawSampleBlock(float[] Samples, int FloatCount, SdrSampleMetadata Metadata);

    public Rx888SampleDispatcher(
        Action<short[], short[], uint> samplesReceived,
        int expectedBlockLengthBytes = 0,
        ArrayPool<float>? floatPool = null,
        ArrayPool<short>? shortPool = null,
        Action<SdrSampleBlock>? sampleBlockReceived = null)
    {
        _samplesReceived = samplesReceived ?? throw new ArgumentNullException(nameof(samplesReceived));
        _sampleBlockReceived = sampleBlockReceived;
        _expectedBlockLengthBytes = Math.Max(0, expectedBlockLengthBytes);
        _floatPool = floatPool ?? ArrayPool<float>.Shared;
        _shortPool = shortPool ?? ArrayPool<short>.Shared;
    }

    public int QueuedBlockCount => Math.Max(
        0,
        (int)Math.Min(int.MaxValue,
            Interlocked.Read(ref _enqueuedBlocks) - Interlocked.Read(ref _dequeuedBlocks)));

    public long CallbackCount => Interlocked.Read(ref _callbackCount);
    public long DroppedCallbackCount => Interlocked.Read(ref _droppedCallbackCount);
    public int LastCallbackLengthBytes => Volatile.Read(ref _lastCallbackLengthBytes);
    public long UnexpectedCallbackLengthCount => Interlocked.Read(ref _unexpectedCallbackLengthCount);

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
            Volatile.Write(ref _sourceSampleCount, 0);
            Volatile.Write(ref _droppedCallbackCount, 0);
            Volatile.Write(ref _enqueuedBlocks, 0);
            Volatile.Write(ref _dequeuedBlocks, 0);
            Volatile.Write(ref _lastCallbackTimestamp, 0);
            Volatile.Write(ref _lastCallbackLengthBytes, 0);
            Volatile.Write(ref _unexpectedCallbackLengthCount, 0);
            _cancellation = new CancellationTokenSource();
            _queue = Channel.CreateBounded<RawSampleBlock>(
                new BoundedChannelOptions(QueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
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

    public bool TryEnqueue(IntPtr source, int byteLength)
    {
        SdrSampleMetadata metadata = RecordCallback(byteLength);
        if (source == IntPtr.Zero || byteLength < 2 * sizeof(float) ||
            byteLength % (2 * sizeof(float)) != 0)
        {
            Interlocked.Increment(ref _droppedCallbackCount);
            return false;
        }

        int floatCount = byteLength / sizeof(float);
        float[] raw = _floatPool.Rent(floatCount);
        try
        {
            Marshal.Copy(source, raw, 0, floatCount);
            return TryEnqueueOwned(raw, floatCount, metadata);
        }
        catch (Exception exception)
        {
            _floatPool.Return(raw, clearArray: false);
            Interlocked.Increment(ref _droppedCallbackCount);
            Debug.WriteLine($"[Rx888Mk2Controller] Failed to copy an IQ callback block: {exception.Message}");
            return false;
        }
    }

    internal bool TryEnqueue(ReadOnlySpan<float> source)
    {
        int byteLength = checked(source.Length * sizeof(float));
        SdrSampleMetadata metadata = RecordCallback(byteLength);
        if (source.Length < 2 || (source.Length & 1) != 0)
        {
            Interlocked.Increment(ref _droppedCallbackCount);
            return false;
        }

        float[] raw = _floatPool.Rent(source.Length);
        source.CopyTo(raw);
        return TryEnqueueOwned(raw, source.Length, metadata);
    }

    private bool TryEnqueueOwned(float[] raw, int floatCount, SdrSampleMetadata metadata)
    {
        lock (_gate)
        {
            Channel<RawSampleBlock>? queue = _queue;
            if (queue is not null && queue.Writer.TryWrite(new RawSampleBlock(raw, floatCount, metadata)))
            {
                Interlocked.Increment(ref _enqueuedBlocks);
                return true;
            }
        }

        _floatPool.Return(raw, clearArray: false);
        Interlocked.Increment(ref _droppedCallbackCount);
        return false;
    }

    private SdrSampleMetadata RecordCallback(int byteLength)
    {
        long sequence = Interlocked.Increment(ref _callbackCount);
        int sampleCount = Math.Max(0, byteLength / (2 * sizeof(float)));
        long start = Interlocked.Add(ref _sourceSampleCount, sampleCount) - sampleCount;
        Interlocked.Exchange(ref _lastCallbackTimestamp, Stopwatch.GetTimestamp());
        Volatile.Write(ref _lastCallbackLengthBytes, byteLength);
        if (_expectedBlockLengthBytes > 0 && byteLength != _expectedBlockLengthBytes)
            Interlocked.Increment(ref _unexpectedCallbackLengthCount);
        return new SdrSampleMetadata(sequence, start);
    }

    private void Dispatch(Channel<RawSampleBlock> queue, CancellationToken cancellationToken)
    {
        var continuity = new SdrSampleDeliveryTracker();
        try
        {
            try
            {
                Thread.CurrentThread.Name ??= "RX888 IQ dispatcher";
                Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[Rx888Mk2Controller] Failed to configure IQ dispatcher: {exception.Message}");
            }

            while (!cancellationToken.IsCancellationRequested &&
                   queue.Reader.WaitToReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
            {
                while (!cancellationToken.IsCancellationRequested &&
                       queue.Reader.TryRead(out RawSampleBlock block))
                {
                    Interlocked.Increment(ref _dequeuedBlocks);
                    DispatchBlock(block, continuity, cancellationToken);
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
                _floatPool.Return(block.Samples, clearArray: false);
            }
            try { Thread.CurrentThread.Priority = ThreadPriority.Normal; }
            catch { }
        }
    }

    private void DispatchBlock(
        RawSampleBlock block,
        SdrSampleDeliveryTracker continuity,
        CancellationToken cancellationToken)
    {
        int sampleCount = block.FloatCount / 2;
        short[] samplesI = _shortPool.Rent(sampleCount);
        short[] samplesQ = _shortPool.Rent(sampleCount);
        try
        {
            try
            {
                ConvertInterleavedFloatIq(
                    block.Samples.AsSpan(0, block.FloatCount),
                    samplesI.AsSpan(0, sampleCount),
                    samplesQ.AsSpan(0, sampleCount));
            }
            finally
            {
                _floatPool.Return(block.Samples, clearArray: false);
            }

            try
            {
                if (cancellationToken.IsCancellationRequested) return;
                SdrSampleMetadata metadata = continuity.Observe(block.Metadata, (uint)sampleCount);
                _sampleBlockReceived?.Invoke(new SdrSampleBlock(
                    samplesI, samplesQ, (uint)sampleCount, metadata));
                _samplesReceived(samplesI, samplesQ, (uint)sampleCount);
            }
            catch (Exception exception)
            {
                continuity.MarkFailedDelivery();
                Debug.WriteLine($"[Rx888Mk2Controller] IQ consumer failed: {exception}");
            }
        }
        finally
        {
            _shortPool.Return(samplesI, clearArray: false);
            _shortPool.Return(samplesQ, clearArray: false);
        }
    }

    internal static void ConvertInterleavedFloatIq(
        ReadOnlySpan<float> raw,
        Span<short> samplesI,
        Span<short> samplesQ)
    {
        int sampleCount = Math.Min(raw.Length / 2, Math.Min(samplesI.Length, samplesQ.Length));
        for (int index = 0; index < sampleCount; index++)
        {
            samplesI[index] = FloatToShort(raw[index * 2]);
            samplesQ[index] = FloatToShort(raw[index * 2 + 1]);
        }
    }

    private static short FloatToShort(float value) =>
        (short)MathF.Round(Math.Clamp(value, -1.0f, 1.0f) * short.MaxValue);

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
                    Debug.WriteLine("[Rx888Mk2Controller] IQ dispatcher stop timed out.");
            }
            catch (AggregateException exception)
            {
                Debug.WriteLine($"[Rx888Mk2Controller] IQ dispatcher stopped with error: {exception.InnerException?.Message ?? exception.Message}");
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
