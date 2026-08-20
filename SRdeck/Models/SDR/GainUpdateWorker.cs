using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SRdeck.Models.SDR;

public interface IGainUpdateWorker : IDisposable
{
    void RequestUpdate();
}

public interface IGainUpdateWorkerFactory
{
    IGainUpdateWorker Create(Action applyUpdate);
}

public sealed class GainUpdateWorkerFactory : IGainUpdateWorkerFactory
{
    public IGainUpdateWorker Create(Action applyUpdate) => new GainUpdateWorker(applyUpdate);
}

internal sealed class GainUpdateWorker : IGainUpdateWorker
{
    private readonly Action _applyUpdate;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Task _workerTask;
    private int _pending;
    private int _signalPosted;
    private int _disposed;

    public GainUpdateWorker(Action applyUpdate)
    {
        _applyUpdate = applyUpdate ?? throw new ArgumentNullException(nameof(applyUpdate));
        _workerTask = Task.Run(RunAsync);
    }

    public void RequestUpdate()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        Interlocked.Exchange(ref _pending, 1);
        if (Interlocked.Exchange(ref _signalPosted, 1) == 0)
        {
            _signal.Release();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                await _signal.WaitAsync(_cancellation.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _signalPosted, 0);

                while (Interlocked.Exchange(ref _pending, 0) == 1)
                {
                    try
                    {
                        _applyUpdate();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[GainUpdateWorker] Gain update failed: {ex}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cancellation.Cancel();
        _signal.Release();
        try
        {
            if (!_workerTask.Wait(TimeSpan.FromSeconds(2)))
            {
                Debug.WriteLine("[GainUpdateWorker] Timed out while stopping.");
            }
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1 && ex.InnerException is OperationCanceledException)
        {
        }
        finally
        {
            _signal.Dispose();
            _cancellation.Dispose();
        }
    }
}
