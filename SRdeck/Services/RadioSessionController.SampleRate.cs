using System.Diagnostics;
using SRdeck.Models;

namespace SRdeck.Services;

public interface ISdrSampleRateController
{
    bool IsPlaybackInput { get; }
    bool TryPrepareSampleRate(
        RadioControl control,
        long minimumSampleRateHz,
        out RadioControl prepared,
        out string error);
    Task<RadioSessionStartResult> ApplySampleRateAsync(
        RadioControl control,
        CancellationToken cancellationToken = default);
}

public sealed partial class RadioSessionController
{
    public bool IsPlaybackInput => _engine.IsPlaying;

    public bool TryPrepareSampleRate(
        RadioControl control,
        long minimumSampleRateHz,
        out RadioControl prepared,
        out string error)
    {
        prepared = control;
        if (_engine.IsPlaying)
        {
            error = "録音ファイルのサンプルレートは変更できません。必要なレート以上の録音を開くか、SDR入力へ切り替えてください。";
            return false;
        }
        if (_engine.SdrDevice is not { } device)
        {
            error = "サンプルレートを変更するにはSDRデバイスを接続してください。";
            return false;
        }

        bool isRtlDevice = device.Capabilities.IsRtlSdr;
        var rates = SdrSampleRatePolicy.GetSupportedRates(isRtlDevice);
        int sampleRateHz = rates.FirstOrDefault(rate => rate >= minimumSampleRateHz);
        if (sampleRateHz == 0)
        {
            error = $"必要なサンプルレート {minimumSampleRateHz / 1_000_000.0:0.###} MS/s は、" +
                $"このSDRの対応上限 {rates[^1] / 1_000_000.0:0.###} MS/s を超えています。";
            return false;
        }

        prepared.FsHz = sampleRateHz;
        prepared.BaseMainSpanHz = SdrSampleRatePolicy.GetMainSpanHz(sampleRateHz, isRtlDevice);
        prepared.MainSpanHz = prepared.BaseMainSpanHz;
        error = string.Empty;
        return true;
    }

    public async Task<RadioSessionStartResult> ApplySampleRateAsync(
        RadioControl control,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool configurationAttempted = false;
        try
        {
            if (!TryPrepareSampleRate(control, control.FsHz, out RadioControl prepared, out string error))
                return new(false, error);
            if (prepared.FsHz != control.FsHz)
                return new(false, "このSDRは指定されたサンプルレートに対応していません。");

            ISdrDevice device = _engine.SdrDevice!;
            RadioControl previousControl = _engine.Control;
            int previousDeviceRate = device.FsHz;
            long previousDeviceCenter = device.CenterFreqHz;
            bool resumeStream = _engine.IsSdrRunning;
            configurationAttempted = true;
            _generation++;
            try
            {
                if (resumeStream)
                    await Task.Run(_transitionCoordinator.StopCurrentSession).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (!device.ApplySampleRate(control.FsHz) || device.FsHz != control.FsHz)
                    throw new InvalidOperationException("SDRのサンプルレート設定に失敗しました。");
                device.CenterFreqHz = control.CenterFreqHz;
                _engine.Control = control;
                _engine.EnsureIqBufferCapacity();
                _engine.ResetPointersForRestart();

                if (resumeStream)
                {
                    RadioSessionStartResult restart = await _sdrSessionStarter.StartAsync().ConfigureAwait(false);
                    if (!restart.Success)
                        throw new InvalidOperationException(restart.Error ?? "SDRの受信再開に失敗しました。");
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception)
            {
                string? restoreError = await RestoreSampleRateAsync(
                    device, previousControl, previousDeviceRate, previousDeviceCenter, resumeStream)
                    .ConfigureAwait(false);
                if (exception is OperationCanceledException && restoreError is null) throw;
                return new(false, restoreError is null
                    ? exception.Message
                    : $"{exception.Message}\n元の受信設定への復元に失敗しました: {restoreError}");
            }

            _engine.InitialAppSettings.SdrPlaySampleRateHz = control.FsHz;
            _settingsService.SaveSettings(_engine.InitialAppSettings);
            return new(true);
        }
        finally
        {
            _operationGate.Release();
            if (configurationAttempted) SdrConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<string?> RestoreSampleRateAsync(
        ISdrDevice device,
        RadioControl control,
        int sampleRateHz,
        long centerFrequencyHz,
        bool resumeStream)
    {
        try
        {
            if (_engine.IsSdrRunning)
                await Task.Run(_transitionCoordinator.StopCurrentSession).ConfigureAwait(false);
            bool restored = device.ApplySampleRate(sampleRateHz) && device.FsHz == sampleRateHz;
            device.CenterFreqHz = centerFrequencyHz;
            _engine.Control = control;
            _engine.EnsureIqBufferCapacity();
            _engine.ResetPointersForRestart();
            if (!restored) return "SDRのサンプルレートを元に戻せませんでした。";
            if (resumeStream)
            {
                RadioSessionStartResult restart = await _sdrSessionStarter.StartAsync().ConfigureAwait(false);
                if (!restart.Success) return restart.Error ?? "SDRの受信を再開できませんでした。";
            }
            return null;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[RadioSessionController] Failed to restore sample rate: {exception}");
            return exception.Message;
        }
    }
}
