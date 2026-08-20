using System;
using System.Diagnostics;
using System.Threading.Tasks;
using SRdeck.Models;
using SRdeck.Services.Plugins;

namespace SRdeck.Services;

public interface ISdrSessionStarter
{
    Task<RadioSessionStartResult> StartAsync();
}

public sealed class SdrSessionStarter : ISdrSessionStarter
{
    private readonly IRadioSessionEngine _engine;
    private readonly IAudioService _audioService;
    private readonly IRadioSessionTransitionCoordinator _transitionCoordinator;
    private readonly IPluginIqDispatcher? _pluginIqDispatcher;

    public SdrSessionStarter(
        IRadioSessionEngine engine,
        IAudioService audioService,
        IRadioSessionTransitionCoordinator transitionCoordinator,
        IPluginIqDispatcher? pluginIqDispatcher = null)
    {
        _engine = engine;
        _audioService = audioService;
        _transitionCoordinator = transitionCoordinator;
        _pluginIqDispatcher = pluginIqDispatcher;
    }

    public async Task<RadioSessionStartResult> StartAsync()
    {
        _transitionCoordinator.StopCurrentSession();

        if (_engine.SdrDevice == null)
        {
            return new RadioSessionStartResult(false, "SDRデバイスが初期化されていません。");
        }

        _transitionCoordinator.PrepareForSdrStart();
        if (!_engine.TryStartSdrSession())
        {
            return new RadioSessionStartResult(false, "別の入力セッションが動作中です。");
        }

        try
        {
            bool isDeviceStarted = await Task.Run(() =>
            {
                RadioControl control = _engine.Control;
                try
                {
                    _engine.WarmUpForSdrStart();
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"[SdrSessionStarter] FFT warm-up skipped: {exception.Message}");
                }
                _pluginIqDispatcher?.WarmUpActiveChannels(
                    control.FsHz, control.CenterFreqHz);
                return _engine.SdrDevice.Start();
            });
            if (!isDeviceStarted)
            {
                _transitionCoordinator.HandleSdrStartFailure();
                return new RadioSessionStartResult(false, "SDRデバイスを開始できませんでした。");
            }

            _audioService.PlayOutput();
            return new RadioSessionStartResult(true);
        }
        catch (Exception exception)
        {
            _transitionCoordinator.HandleSdrStartFailure();
            return new RadioSessionStartResult(false, $"SDRデバイスの開始に失敗しました:\n{exception}");
        }
    }
}
