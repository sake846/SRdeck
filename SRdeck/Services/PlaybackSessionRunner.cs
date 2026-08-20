using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SRdeck.Models;

namespace SRdeck.Services;

public interface IPlaybackSessionRunner
{
    event EventHandler? PlaybackEnded;
    void Run(long generation, SemaphoreSlim operationGate, Func<long> getGeneration);
}

public sealed class PlaybackSessionRunner : IPlaybackSessionRunner
{
    private readonly IRadioSessionEngine _engine;
    private readonly IRadioSessionTransitionCoordinator _transitionCoordinator;

    public PlaybackSessionRunner(
        IRadioSessionEngine engine,
        IRadioSessionTransitionCoordinator transitionCoordinator)
    {
        _engine = engine;
        _transitionCoordinator = transitionCoordinator;
    }

    public event EventHandler? PlaybackEnded;

    public void Run(long generation, SemaphoreSlim operationGate, Func<long> getGeneration)
    {
        _ = RunCoreAsync(generation, operationGate, getGeneration);
    }

    private async Task RunCoreAsync(long generation, SemaphoreSlim operationGate, Func<long> getGeneration)
    {
        try
        {
            await _engine.ManageAudioBufferAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[PlaybackSessionRunner] Playback loop failed: {exception}");
        }

        bool shouldNotifyEnded = false;
        await operationGate.WaitAsync();
        try
        {
            if (getGeneration() == generation)
            {
                _transitionCoordinator.StopCurrentSession();
                shouldNotifyEnded = true;
            }
        }
        finally
        {
            operationGate.Release();
        }

        if (shouldNotifyEnded)
        {
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        }
    }
}
