using System.Diagnostics;
using SRdeck.Models;

namespace SRdeck.Services;

public interface IPlaybackSessionRunner
{
    event EventHandler? PlaybackEnded;
    Task Run(long generation, SemaphoreSlim operationGate, Func<long> getGeneration);
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

    public Task Run(long generation, SemaphoreSlim operationGate, Func<long> getGeneration)
    {
        Task processing = RunProcessingAsync();
        _ = CompleteAsync(processing, generation, operationGate, getGeneration);
        return processing;
    }

    private async Task RunProcessingAsync()
    {
        try
        {
            await _engine.ManageAudioBufferAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[PlaybackSessionRunner] Playback loop failed: {exception}");
        }
    }

    private async Task CompleteAsync(Task processing, long generation, SemaphoreSlim operationGate, Func<long> getGeneration)
    {
        await processing;
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
