using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Audio;
using SRdeck.Messages;
using SRdeck.Models;

namespace SRdeck.Services;

public sealed record PlaybackProcessingRequest(
    Action ResetProcessingState,
    Func<int> GetFallbackCenterFrequencyHz,
    Action<short[], short[], int, double, int> ProcessSamples,
    Action<string?> NotifyFileChanged);

public interface IPlaybackProcessor
{
    Task RunAsync(PlaybackProcessingRequest request);
}

public sealed class PlaybackProcessor : IPlaybackProcessor
{
    private readonly IAudioOutput _audioOutput;
    private readonly IAudioFileReader _audioFileReader;
    private readonly IInputSessionStateMachine _inputSessionState;

    public PlaybackProcessor(
        IAudioOutput audioOutput,
        IAudioFileReader audioFileReader,
        IInputSessionStateMachine inputSessionState)
    {
        _audioOutput = audioOutput;
        _audioFileReader = audioFileReader;
        _inputSessionState = inputSessionState;
    }

    public async Task RunAsync(PlaybackProcessingRequest request)
    {
        request.ResetProcessingState();
        var stopwatch = Stopwatch.StartNew();
        long totalBlocksProcessed = 0L;
        const int processingIntervalMs = AppConstants.FILE_PROCESSING_INTERVAL_MS;

        while (_inputSessionState.IsPlaying)
        {
            try
            {
                if (!_audioFileReader.IsPlaying)
                {
                    if (_audioOutput.GetBufferedBytes() <= 0)
                    {
                        break;
                    }

                    await Task.Delay(5);
                    continue;
                }

                long expectedBlocks = stopwatch.ElapsedMilliseconds / processingIntervalMs;
                if (totalBlocksProcessed <= expectedBlocks)
                {
                    ProcessPlaybackBlock(request);
                    totalBlocksProcessed++;
                }
                else
                {
                    await Task.Delay(10);
                }
            }
            catch (Exception exception)
            {
                Debug.Print($"Playback processing failed: {exception}");
                WeakReferenceMessenger.Default.Send(
                    new SdrErrorMessage($"再生中にエラーが発生しました:\n{exception}"));
                break;
            }
        }

        _inputSessionState.Stop(InputSessionState.PlayingFile);
    }

    private void ProcessPlaybackBlock(PlaybackProcessingRequest request)
    {
        using PlaybackIqBlock? block = PlaybackIqBlockReader.TryRead(
            _audioFileReader,
            request.GetFallbackCenterFrequencyHz());
        if (block == null)
        {
            return;
        }

        request.ProcessSamples(
            block.ISamples,
            block.QSamples,
            block.SampleCount,
            block.SystemGainDb,
            block.RfFrequencyHz);
        if (block.DidFileChange)
        {
            request.NotifyFileChanged(block.CurrentFileName);
        }
    }
}
