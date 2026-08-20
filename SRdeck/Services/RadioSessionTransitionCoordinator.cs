using System;
using System.Diagnostics;
using SRdeck.Models;

namespace SRdeck.Services;

public interface IRadioSessionTransitionCoordinator
{
    void StopCurrentSession();
    void PrepareForSdrStart();
    void HandleSdrStartFailure();
    void HandlePlaybackStartFailure();
    void SyncControlToPlaybackSampleRate();
}

public sealed class RadioSessionTransitionCoordinator : IRadioSessionTransitionCoordinator
{
    private readonly IRadioSessionEngine _engine;
    private readonly IAudioService _audioService;

    public RadioSessionTransitionCoordinator(
        IRadioSessionEngine engine,
        IAudioService audioService)
    {
        _engine = engine;
        _audioService = audioService;
    }

    public void StopCurrentSession()
    {
        InputSessionState state = _engine.SessionState;
        if (state == InputSessionState.ReceivingSdr)
        {
            _engine.StopSdrSession();
            StopSdrDevice();
        }
        else if (state == InputSessionState.PlayingFile)
        {
            _engine.StopPlaybackSession();
        }

        CloseAudioFileReader();
        StopAudioOutput();
    }

    public void PrepareForSdrStart()
    {
        SyncControlToSdrSampleRate();
        ResetRadioState();
        _engine.EnsureIqBufferCapacity();
        _engine.ResetPointersForRestart();
    }

    public void HandleSdrStartFailure()
    {
        _engine.StopSdrSession();
        StopSdrDevice();
    }

    public void HandlePlaybackStartFailure()
    {
        CloseAudioFileReader();
        _engine.StopPlaybackSession();
    }

    public void SyncControlToPlaybackSampleRate()
    {
        int sampleRate = _audioService.PlaybackSampleRateHz;
        if (sampleRate <= 0) return;

        RadioControl control = _engine.Control;
        if (control.FsHz == sampleRate) return;

        control.FsHz = sampleRate;
        _engine.Control = control;
        _engine.EnsureIqBufferCapacity();
    }

    private void SyncControlToSdrSampleRate()
    {
        int sampleRate = _engine.SdrDevice?.FsHz ?? 0;
        if (sampleRate <= 0) return;

        RadioControl control = _engine.Control;
        if (control.FsHz == sampleRate) return;

        control.FsHz = sampleRate;
        _engine.Control = control;
    }

    private void StopSdrDevice()
    {
        try
        {
            _engine.SdrDevice?.Stop();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[RadioSessionTransitionCoordinator] Failed to stop SDR device: {exception}");
        }
    }

    private void CloseAudioFileReader()
    {
        try
        {
            _audioService.ClosePlayback();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[RadioSessionTransitionCoordinator] Failed to close playback reader: {exception}");
        }
    }

    private void StopAudioOutput()
    {
        try
        {
            _audioService.StopOutput();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[RadioSessionTransitionCoordinator] Failed to stop audio output: {exception}");
        }
    }

    private void ResetRadioState()
    {
        RadioState state = _engine.State.CreateSnapshot();
        state.MinFftPwr = AppConstants.MIN_RSSI_DB;
        state.Min2FftPwr = AppConstants.MIN_RSSI_DB;
        state.AveDb = AppConstants.MIN_RSSI_DB;
        state.Ave2Db = AppConstants.MIN_RSSI_DB;
        state.MaxDb = AppConstants.MIN_RSSI_DB;
        _engine.State = state;
    }
}
