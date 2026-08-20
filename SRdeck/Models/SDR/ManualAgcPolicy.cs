using System;

namespace SRdeck.Models.SDR;

internal enum ManualAgcDeviceKind
{
    Generic,
    RtlSdr
}

internal readonly record struct ManualAgcInput(
    short IMax,
    short IMin,
    short QMax,
    short QMin,
    int CurrentGain,
    int MinGain,
    int MaxGain,
    int UpperThreshold,
    int LowerThreshold,
    ManualAgcDeviceKind DeviceKind);

internal static class ManualAgcPolicy
{
    public static bool IsOver(ManualAgcInput input) =>
        input.IMax > input.UpperThreshold || input.IMin < -input.UpperThreshold ||
        input.QMax > input.UpperThreshold || input.QMin < -input.UpperThreshold;

    public static bool IsLow(ManualAgcInput input) =>
        input.IMax < input.LowerThreshold && input.IMin > -input.LowerThreshold &&
        input.QMax < input.LowerThreshold && input.QMin > -input.LowerThreshold;

    public static int CalculateNextGain(ManualAgcInput input)
    {
        bool isOver = IsOver(input);
        bool isLow = IsLow(input);

        if (input.DeviceKind is ManualAgcDeviceKind.RtlSdr)
        {
            if (isOver && input.CurrentGain > input.MinGain)
            {
                return Math.Max(input.MinGain, input.CurrentGain - 6);
            }

            if (isLow && input.CurrentGain < input.MaxGain)
            {
                return Math.Min(input.MaxGain, input.CurrentGain + 5);
            }

            return input.CurrentGain;
        }

        if (isOver && input.CurrentGain < input.MaxGain)
        {
            return Math.Min(input.MaxGain, input.CurrentGain + 6);
        }

        if (isLow && input.CurrentGain > input.MinGain)
        {
            return Math.Max(input.MinGain, input.CurrentGain - 1);
        }

        return input.CurrentGain;
    }
}

