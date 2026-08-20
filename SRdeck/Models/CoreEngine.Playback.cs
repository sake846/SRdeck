using System;
using System.Threading;
using System.Threading.Tasks;
using SRdeck.Services;

namespace SRdeck.Models;

public partial class CoreEngine
{
    private int _lastPlaybackRfFrequencyHz;

    public Task ManageAudioBufferAsync() => _audioService.RunPlaybackAsync(
        new PlaybackProcessingRequest(
            ResetPlaybackBufferState,
            () => Control.CenterFreqHz,
            ProcessPlaybackInputSamples,
            fileName => OnTitleChanged?.Invoke(fileName)));

    private void ResetPlaybackBufferState()
    {
        // ファイル再生は 100ms ブロック境界で処理するため、開始時にリングポインタを境界へ揃える。
        Volatile.Write(ref _lastPlaybackRfFrequencyHz, 0);
        BufferWPtr = 0;
        TotalSamplesReceived = 0;
        ResetPointersForRestart();
        Array.Clear(WaterfallFftData, 0, WaterfallFftData.Length);
        Array.Clear(SpectrumFftData, 0, SpectrumFftData.Length);
    }

    private void SyncPlaybackFrequencyToControl(int rfFrequencyHz)
    {
        OnTitleChanged?.Invoke(_audioService.CurrentPlaybackFileName);
        if (rfFrequencyHz <= 0) return;

        // This callback runs for every playback block.  CenterFreqHz is also the
        // logical center of a panned/zoomed main view, so repeatedly replacing it
        // with the file's RF center breaks cursor-anchored wheel zoom.  Synchronize
        // the UI only when playback metadata actually changes its RF frequency.
        int previousRfFrequencyHz = Interlocked.Exchange(ref _lastPlaybackRfFrequencyHz, rfFrequencyHz);
        if (previousRfFrequencyHz == rfFrequencyHz || Control.CenterFreqHz == rfFrequencyHz) return;

        var radioControl = Control; radioControl.CenterFreqHz = rfFrequencyHz;
        long step = radioControl.StepHz;
        long tunedFrequencyHz = (radioControl.CenterFreqHz + radioControl.FreqOffsetHz + step / 2) / step * step;
        radioControl.TunedFreqHz = (int)tunedFrequencyHz;
        radioControl.FreqOffsetHz = radioControl.TunedFreqHz - radioControl.CenterFreqHz;
        Control = radioControl;
        OnFileFrequencyChanged?.Invoke();
    }
}
