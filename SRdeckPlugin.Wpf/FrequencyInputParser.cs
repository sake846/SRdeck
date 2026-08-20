using System.Globalization;

namespace SRdeckPlugin.Wpf;

public enum FrequencyInputUnit
{
    Hz,
    KiloHertz,
    MegaHertz
}

public static class FrequencyInputParser
{
    public static bool TryParse(string input, FrequencyInputUnit defaultUnit, out long frequencyHz)
    {
        frequencyHz = 0;
        string text = input.Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        FrequencyInputUnit unit = defaultUnit;

        if (text.Length > 0)
        {
            char suffix = text[^1];
            if (suffix is 'm' or 'M')
            {
                unit = FrequencyInputUnit.MegaHertz;
                text = text[..^1];
            }
            else if (suffix is 'k' or 'K')
            {
                unit = FrequencyInputUnit.KiloHertz;
                text = text[..^1];
            }
            else if (suffix is 'h' or 'H')
            {
                unit = FrequencyInputUnit.Hz;
                text = text[..^1];
            }
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ||
            !double.IsFinite(value) || value <= 0)
            return false;

        double multiplier = unit switch
        {
            FrequencyInputUnit.MegaHertz => 1_000_000d,
            FrequencyInputUnit.KiloHertz => 1_000d,
            _ => 1d
        };
        double scaled = value * multiplier;
        if (!double.IsFinite(scaled) || scaled < 1 || scaled > int.MaxValue)
            return false;

        frequencyHz = (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
        return frequencyHz is > 0 and <= int.MaxValue;
    }
}
