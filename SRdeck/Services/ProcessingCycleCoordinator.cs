using System;
using System.Threading;

namespace SRdeck.Services;

public interface IProcessingCycleCoordinator
{
    bool TryRun(Func<bool> canRun, Action runCycle);
    void StopAndWait();
}

public sealed class ProcessingCycleCoordinator : IProcessingCycleCoordinator
{
    private readonly object _sync = new();
    private bool _isRunning;
    private bool _isStopped;

    public bool TryRun(Func<bool> canRun, Action runCycle)
    {
        ArgumentNullException.ThrowIfNull(canRun);
        ArgumentNullException.ThrowIfNull(runCycle);

        lock (_sync)
        {
            if (_isStopped || _isRunning || !canRun())
            {
                return false;
            }

            _isRunning = true;
        }

        try
        {
            runCycle();
            return true;
        }
        finally
        {
            lock (_sync)
            {
                _isRunning = false;
                Monitor.PulseAll(_sync);
            }
        }
    }

    public void StopAndWait()
    {
        lock (_sync)
        {
            _isStopped = true;
            while (_isRunning)
            {
                Monitor.Wait(_sync);
            }
        }
    }
}
