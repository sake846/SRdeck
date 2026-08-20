using System;
using System.Threading.Tasks;
using SRdeck.Models;

namespace SRdeck.Services;

public interface IPlaybackSessionStarter
{
    Task<RadioSessionStartResult> StartAsync(string filePath, double startSeconds);
}

public sealed class PlaybackSessionStarter : IPlaybackSessionStarter
{
    private readonly IRadioSessionEngine _engine;
    private readonly IAudioService _audioService;
    private readonly IRadioSessionTransitionCoordinator _transitionCoordinator;

    public PlaybackSessionStarter(
        IRadioSessionEngine engine,
        IAudioService audioService,
        IRadioSessionTransitionCoordinator transitionCoordinator)
    {
        _engine = engine;
        _audioService = audioService;
        _transitionCoordinator = transitionCoordinator;
    }

    public Task<RadioSessionStartResult> StartAsync(string filePath, double startSeconds)
    {
        _transitionCoordinator.StopCurrentSession();

        try
        {
            if (!_audioService.OpenPlayback(filePath, startSeconds))
            {
                return Task.FromResult(new RadioSessionStartResult(false, "ファイルのオープンに失敗しました。"));
            }

            if (!_engine.TryStartPlaybackSession())
            {
                _transitionCoordinator.StopCurrentSession();
                return Task.FromResult(new RadioSessionStartResult(false, "別の入力セッションが動作中です。"));
            }

            _transitionCoordinator.SyncControlToPlaybackSampleRate();
            _audioService.PlayOutput();
            return Task.FromResult(new RadioSessionStartResult(true));
        }
        catch (Exception exception)
        {
            _transitionCoordinator.HandlePlaybackStartFailure();
            return Task.FromResult(new RadioSessionStartResult(false, $"再生の開始に失敗しました:\n{exception}"));
        }
    }
}
