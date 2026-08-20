using System.Threading.Channels;
using SRdeckPlugin.Contracts;
using SRdeck.Audio;

namespace SRdeck.Services.Plugins;

public readonly record struct PluginAudioSnapshot(
    long SubmittedFrames,
    long PlayedFrames,
    long DroppedFrames,
    int QueueDepth,
    string? LastError);

public interface IPluginAudioSinkFactory
{
    IPluginAudioSink Create(string pluginId);
    PluginAudioSnapshot GetSnapshot(string pluginId);
}

public sealed class PluginAudioRouter : IPluginAudioSinkFactory, IDisposable
{
    private const int QueueCapacity = 16;
    private readonly Func<IPluginManager> _pluginManager;
    private readonly IAudioOutput _audioOutput;
    private readonly bool _disposeAudioOutput;
    private readonly Channel<AudioEnvelope> _channel;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;
    private readonly Dictionary<string, Counters> _counters = new(StringComparer.Ordinal);
    private readonly object _counterGate = new();
    private int _outputSampleRateHz;
    private int _outputChannels;
    private int _queueDepth;
    private int _disposed;

    public PluginAudioRouter(
        Func<IPluginManager> pluginManager,
        IAudioOutput audioOutput,
        bool disposeAudioOutput = false)
    {
        _pluginManager = pluginManager;
        _audioOutput = audioOutput;
        _disposeAudioOutput = disposeAudioOutput;
        _channel = Channel.CreateBounded<AudioEnvelope>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(ProcessAsync);
    }

    public IPluginAudioSink Create(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (_counterGate) _counters.TryAdd(pluginId, new Counters());
        return new PluginAudioSink(this, pluginId);
    }

    public PluginAudioSnapshot GetSnapshot(string pluginId)
    {
        lock (_counterGate)
        {
            if (!_counters.TryGetValue(pluginId, out Counters? counters)) return default;
            return new PluginAudioSnapshot(
                Interlocked.Read(ref counters.SubmittedFrames),
                Interlocked.Read(ref counters.PlayedFrames),
                Interlocked.Read(ref counters.DroppedFrames),
                Volatile.Read(ref _queueDepth),
                Volatile.Read(ref counters.LastError));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel.Writer.TryComplete();
        _cancellation.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        while (_channel.Reader.TryRead(out _)) Interlocked.Decrement(ref _queueDepth);
        if (_disposeAudioOutput) _audioOutput.Dispose();
        _cancellation.Dispose();
    }

    private bool TrySubmit(string pluginId, PcmAudioFrame frame)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            !string.Equals(frame.PluginId, pluginId, StringComparison.Ordinal) ||
            !IsValid(frame)) return false;
        IPluginManager manager = _pluginManager();
        if (manager.ActivePluginId != pluginId || !manager.IsActivePluginStreaming)
        {
            return false;
        }

        byte[] ownedData = frame.Data.ToArray();
        var ownedFrame = frame with { Data = ownedData };
        Counters counters = GetCounters(pluginId);
        Interlocked.Increment(ref _queueDepth);
        if (!_channel.Writer.TryWrite(new AudioEnvelope(pluginId, ownedFrame)))
        {
            Interlocked.Decrement(ref _queueDepth);
            Interlocked.Increment(ref counters.DroppedFrames);
            return false;
        }
        Interlocked.Increment(ref counters.SubmittedFrames);
        return true;
    }

    private bool _isPrefilling;

    private void Reset(string pluginId)
    {
        while (_channel.Reader.TryRead(out AudioEnvelope? envelope))
        {
            if (envelope is null) continue;
            Interlocked.Decrement(ref _queueDepth);
            Interlocked.Increment(ref GetCounters(envelope.PluginId).DroppedFrames);
        }
        if (_pluginManager().ActivePluginId == pluginId)
        {
            _audioOutput.ClearBuffer();
            _isPrefilling = false;
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (AudioEnvelope envelope in _channel.Reader.ReadAllAsync(_cancellation.Token).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _queueDepth);
                Counters counters = GetCounters(envelope.PluginId);
                try
                {
                    IPluginManager manager = _pluginManager();
                    if (manager.ActivePluginId != envelope.PluginId || !manager.IsActivePluginStreaming)
                    {
                        Interlocked.Increment(ref counters.DroppedFrames);
                        continue;
                    }
                    PcmAudioFrame frame = envelope.Frame;
                    if (_outputSampleRateHz != frame.SampleRateHz || _outputChannels != frame.Channels)
                    {
                        _audioOutput.Initialize(frame.SampleRateHz, frame.Channels);
                        _audioOutput.Play();
                        _outputSampleRateHz = frame.SampleRateHz;
                        _outputChannels = frame.Channels;
                        _isPrefilling = false;
                    }
                    if (frame.IsDiscontinuous)
                    {
                        _audioOutput.ClearBuffer();
                        _isPrefilling = false;
                    }

                    int bytesPerMs = frame.SampleRateHz * frame.Channels * sizeof(short) / 1000;
                    if (bytesPerMs > 0)
                    {
                        int overrunThresholdBytes = bytesPerMs * 200; // 200ms
                        int trimTargetBytes = bytesPerMs * 150;       // 150ms
                        int prefillTargetBytes = bytesPerMs * 150;    // 150ms

                        int bufferedBytes = _audioOutput.GetBufferedBytes();
                        if (bufferedBytes == 0 && !_isPrefilling)
                        {
                            _isPrefilling = true;
                            _audioOutput.SetPlaybackPaused(true);
                        }

                        if (bufferedBytes > overrunThresholdBytes)
                        {
                            _audioOutput.TrimBufferedBytes(trimTargetBytes);
                            bufferedBytes = _audioOutput.GetBufferedBytes();
                            if (bufferedBytes == 0 && !_isPrefilling)
                            {
                                _isPrefilling = true;
                                _audioOutput.SetPlaybackPaused(true);
                            }
                        }

                        byte[] bytes = frame.Data.ToArray();
                        _audioOutput.WriteSamples(bytes, 0, bytes.Length);

                        bufferedBytes = _audioOutput.GetBufferedBytes();
                        if (_isPrefilling && bufferedBytes >= prefillTargetBytes)
                        {
                            _isPrefilling = false;
                            _audioOutput.SetPlaybackPaused(false);
                        }
                    }
                    else
                    {
                        byte[] bytes = frame.Data.ToArray();
                        _audioOutput.WriteSamples(bytes, 0, bytes.Length);
                    }

                    Interlocked.Increment(ref counters.PlayedFrames);
                    Volatile.Write(ref counters.LastError, null);
                }
                catch (Exception exception)
                {
                    Volatile.Write(ref counters.LastError, exception.Message);
                    _pluginManager().ReportFault(envelope.PluginId, "output audio", exception);
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
    }

    private Counters GetCounters(string pluginId)
    {
        lock (_counterGate)
        {
            if (!_counters.TryGetValue(pluginId, out Counters? counters))
            {
                counters = new Counters();
                _counters.Add(pluginId, counters);
            }
            return counters;
        }
    }

    private static bool IsValid(PcmAudioFrame frame) =>
        !string.IsNullOrWhiteSpace(frame.PluginId) &&
        frame.StreamId != Guid.Empty &&
        frame.SampleRateHz is >= 8_000 and <= 384_000 &&
        frame.Channels is 1 or 2 &&
        frame.Format == PcmSampleFormat.Signed16LittleEndian &&
        !frame.Data.IsEmpty &&
        frame.Data.Length % (frame.Channels * sizeof(short)) == 0;

    private sealed class PluginAudioSink(PluginAudioRouter owner, string pluginId) : IPluginAudioSink
    {
        public bool TrySubmit(PcmAudioFrame frame) => owner.TrySubmit(pluginId, frame);
        public void Reset() => owner.Reset(pluginId);
    }

    private sealed class Counters
    {
        public long SubmittedFrames;
        public long PlayedFrames;
        public long DroppedFrames;
        public string? LastError;
    }

    private sealed record AudioEnvelope(string PluginId, PcmAudioFrame Frame);
}
