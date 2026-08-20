namespace SRdeckPlugin.Wpf;

/// <summary>Formats the common rate-conversion rows shown by plugin diagnostics.</summary>
public static class DiagnosticRateDisplay
{
    public static string FormatSampleRate(double sampleRateHz)
    {
        if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0) return "—";
        return sampleRateHz >= 1_000_000
            ? $"{sampleRateHz / 1_000_000.0:F3} MS/s"
            : $"{sampleRateHz / 1_000.0:F1} kS/s";
    }

    public static string FormatPath(double inputSampleRateHz, double intermediateSampleRateHz,
        double demodulationSampleRateHz)
    {
        if (!double.IsFinite(inputSampleRateHz) || inputSampleRateHz <= 0 ||
            !double.IsFinite(demodulationSampleRateHz) || demodulationSampleRateHz <= 0)
            return "—";

        string intermediate = IsDistinct(intermediateSampleRateHz, inputSampleRateHz) &&
                              IsDistinct(intermediateSampleRateHz, demodulationSampleRateHz)
            ? FormatSampleRate(intermediateSampleRateHz)
            : "中間なし";
        return $"{FormatSampleRate(inputSampleRateHz)} → {intermediate} → {FormatSampleRate(demodulationSampleRateHz)}";
    }

    public static string FormatConversion(bool hasRateConversion, string owner) =>
        hasRateConversion ? $"あり（{owner}）" : "なし（1:1）";

    public static bool IsDistinct(double left, double right) =>
        double.IsFinite(left) && left > 0 && double.IsFinite(right) && right > 0 &&
        Math.Abs(left - right) > 0.5;
}
