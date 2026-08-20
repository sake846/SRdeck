using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SRdeck.Models;

public interface ISignalProcessingWorker : IDisposable
{
    void Start();
    void Signal();
}

public interface ISignalProcessingWorkerFactory
{
    ISignalProcessingWorker Create(Action processCycle);
}

public sealed class SignalProcessingWorkerFactory : ISignalProcessingWorkerFactory
{
    public ISignalProcessingWorker Create(Action processCycle) => new SignalProcessingWorker(processCycle);
}

internal sealed class SignalProcessingWorker : ISignalProcessingWorker
{
    private readonly Action _processCycle;
    private readonly SemaphoreSlim _processSignal = new(0);
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _workerTask;
    private int _workerThreadId;
    private int _started;
    private int _disposed;
    private int _resourcesDisposed;

    public SignalProcessingWorker(Action processCycle)
    {
        _processCycle = processCycle ?? throw new ArgumentNullException(nameof(processCycle));
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        _workerTask = Task.Factory.StartNew(
            ExecuteProcessingLoop,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void Signal()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        TryProcessSignalCycle();
    }

    private void TryProcessSignalCycle()
    {
        try
        {
            _processSignal.Release();
        }
        catch (ObjectDisposedException)
        {
            // A racing producer may signal while shutdown completes.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cancellation.Cancel();
        TryProcessSignalCycle();
        Task? workerTask = _workerTask;
        if (workerTask != null && !workerTask.IsCompleted)
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref _workerThreadId))
            {
                return;
            }

            if (!workerTask.Wait(TimeSpan.FromSeconds(3)))
            {
                Debug.WriteLine("[SignalProcessingWorker] Timed out while stopping; resources will be released when the task exits.");
                return;
            }
        }

        DisposeResources();
    }

    private void ExecuteProcessingLoop()
    {
        Volatile.Write(ref _workerThreadId, Environment.CurrentManagedThreadId);
        try
        {
            Thread.CurrentThread.Name ??= "SdrEngine_ProcessingTask";
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[SignalProcessingWorker] Failed to configure worker thread: {exception.Message}");
        }

        uint taskIndex = 0;
        IntPtr avrtHandle = IntPtr.Zero;
        try
        {
            avrtHandle = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
            if (avrtHandle == IntPtr.Zero)
            {
                Debug.WriteLine($"[SignalProcessingWorker] Failed to enable MMCSS. Win32Error: {Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[SignalProcessingWorker] MMCSS is unavailable: {exception.Message}");
        }

        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    _processSignal.Wait(_cancellation.Token);
                    if (_cancellation.IsCancellationRequested) break;
                    _processCycle();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"[SignalProcessingWorker] Processing cycle failed: {exception.Message}");
                }
            }
        }
        finally
        {
            if (avrtHandle != IntPtr.Zero)
            {
                AvRevertMmThreadCharacteristics(avrtHandle);
            }
            try { Thread.CurrentThread.Priority = ThreadPriority.Normal; }
            catch { /* Permissions or thread state issues during shutdown. */ }
            if (Volatile.Read(ref _disposed) != 0)
            {
                DisposeResources();
            }
            Volatile.Write(ref _workerThreadId, 0);
        }
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return;
        _processSignal.Dispose();
        _cancellation.Dispose();
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);
}
