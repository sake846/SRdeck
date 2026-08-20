using System;
using System.Threading;
using System.Threading.Tasks;

namespace SRdeck.Services;

public readonly record struct RadioSessionStartResult(bool Success, string? Error = null);

public interface IRadioSessionController
{
    event EventHandler? PlaybackEnded;
    Task<RadioSessionStartResult> StartSdrAsync();
    Task<RadioSessionStartResult> StartPlaybackAsync(string filePath, double startSeconds);
    Task StopAsync();
}

public sealed class RadioSessionController : IRadioSessionController
{
    private readonly IRadioSessionTransitionCoordinator _transitionCoordinator;
    private readonly ISdrSessionStarter _sdrSessionStarter;
    private readonly IPlaybackSessionStarter _playbackSessionStarter;
    private readonly IPlaybackSessionRunner _playbackSessionRunner;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private long _generation;

    public RadioSessionController(
        IRadioSessionTransitionCoordinator transitionCoordinator,
        ISdrSessionStarter sdrSessionStarter,
        IPlaybackSessionStarter playbackSessionStarter,
        IPlaybackSessionRunner playbackSessionRunner)
    {
        _transitionCoordinator = transitionCoordinator;
        _sdrSessionStarter = sdrSessionStarter;
        _playbackSessionStarter = playbackSessionStarter;
        _playbackSessionRunner = playbackSessionRunner;
        _playbackSessionRunner.PlaybackEnded += (sender, args) => PlaybackEnded?.Invoke(sender, args);
    }

    public event EventHandler? PlaybackEnded;

    public async Task<RadioSessionStartResult> StartSdrAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            _generation++;
            return await _sdrSessionStarter.StartAsync();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<RadioSessionStartResult> StartPlaybackAsync(string filePath, double startSeconds)
    {
        await _operationGate.WaitAsync();
        try
        {
            long generation = ++_generation;
            RadioSessionStartResult result = await _playbackSessionStarter.StartAsync(filePath, startSeconds);
            if (result.Success)
            {
                _playbackSessionRunner.Run(generation, _operationGate, () => _generation);
            }
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            _generation++;
            // Device shutdown can wait for native SDR callbacks to finish.
            // Never perform that potentially blocking work on the WPF UI thread.
            await Task.Run(_transitionCoordinator.StopCurrentSession);
        }
        finally
        {
            _operationGate.Release();
        }
    }
}
