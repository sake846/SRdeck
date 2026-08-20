using System;
using SRdeck.Models;

namespace SRdeck.Services;

public readonly record struct TuningSynchronizationRequest(
    RadioControl Control,
    SdrDeviceCapabilities DeviceCapabilities,
    int DeviceSampleRateHz,
    int PreviousCenterFrequencyHz,
    bool IsPlaying,
    float CurrentCalibrationOffset);

public readonly record struct TuningSynchronizationResult(
    float CalibrationOffset,
    int ReferenceCenterFrequencyHz,
    bool NeedsBackgroundRedraw,
    bool RequiresHardwareFrequencyUpdate);

public readonly record struct InputCenterFrequencyRequest(
    RadioControl Control,
    int PreviousCenterFrequencyHz,
    long DeviceCenterFrequencyHz);

public readonly record struct InputSampleRateRequest(
    bool IsPlaying,
    int PlaybackSampleRateHz,
    int ControlSampleRateHz,
    int DeviceSampleRateHz);

public interface ITuningCoordinator
{
    TuningSynchronizationResult Evaluate(TuningSynchronizationRequest request);
    int ResolveInputCenterFrequency(InputCenterFrequencyRequest request);
    int ResolveSampleRate(InputSampleRateRequest request);
}

public sealed class TuningCoordinator : ITuningCoordinator
{
    public TuningSynchronizationResult Evaluate(TuningSynchronizationRequest request)
    {
        float calibrationOffset = request.CurrentCalibrationOffset;


        bool centerFrequencyChanged =
            !IsMainViewZoomed(request.Control)
            && request.Control.CenterFreqHz != request.PreviousCenterFrequencyHz;
        int referenceCenterFrequencyHz = centerFrequencyChanged
            ? request.Control.CenterFreqHz
            : request.PreviousCenterFrequencyHz;

        return new TuningSynchronizationResult(
            calibrationOffset,
            referenceCenterFrequencyHz,
            NeedsBackgroundRedraw: centerFrequencyChanged,
            RequiresHardwareFrequencyUpdate: centerFrequencyChanged && !request.IsPlaying);
    }

    public int ResolveInputCenterFrequency(InputCenterFrequencyRequest request)
    {
        RadioControl control = request.Control;
        if (!IsMainViewZoomed(control))
        {
            return RoundInputCenterFrequency(control);
        }

        if (request.PreviousCenterFrequencyHz > 0)
        {
            return request.PreviousCenterFrequencyHz;
        }
        if (request.DeviceCenterFrequencyHz > 0)
        {
            return (int)Math.Min(int.MaxValue, request.DeviceCenterFrequencyHz);
        }
        return control.CenterFreqHz;
    }

    public static int RoundInputCenterFrequency(RadioControl control)
    {
        int spanHz = control.MainSpanHz > 0
            ? control.MainSpanHz
            : control.BaseMainSpanHz;
        int roundingHz = GetCenterFrequencyRoundingHz(spanHz);
        long roundedCenterFrequencyHz = ((long)control.CenterFreqHz + roundingHz / 2L)
            / roundingHz
            * roundingHz;
        return (int)Math.Clamp(roundedCenterFrequencyHz, 0, 2_000_000_000L);
    }

    public int ResolveSampleRate(InputSampleRateRequest request)
    {
        if (request.IsPlaying && request.PlaybackSampleRateHz > 0)
        {
            return request.PlaybackSampleRateHz;
        }
        if (request.ControlSampleRateHz > 0)
        {
            return request.ControlSampleRateHz;
        }
        if (request.DeviceSampleRateHz > 0)
        {
            return request.DeviceSampleRateHz;
        }
        return Math.Max(1, (int)AppConstants.FULL_BW);
    }

    private static bool IsMainViewZoomed(RadioControl control) =>
        control.BaseMainSpanHz > 0
        && control.MainSpanHz > 0
        && control.MainSpanHz < control.BaseMainSpanHz;

    private static int GetCenterFrequencyRoundingHz(int spanHz)
    {
        if (spanHz <= 1_000_000) return 100_000;
        if (spanHz <= 2_400_000) return 200_000;
        if (spanHz <= 4_000_000) return 500_000;
        if (spanHz <= 8_000_000) return 500_000;
        if (spanHz <= 16_000_000) return 1_000_000;
        if (spanHz <= 32_000_000) return 2_000_000;
        return 4_000_000;
    }
}
